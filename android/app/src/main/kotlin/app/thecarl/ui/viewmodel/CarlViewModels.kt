package app.thecarl.ui.viewmodel

import app.thecarl.core.data.capture.CaptureOutcome
import app.thecarl.core.data.capture.ManualCaptureRequest
import app.thecarl.core.data.capture.MinorUnits
import app.thecarl.core.data.repository.TransactionCapture
import app.thecarl.core.data.repository.OutboxRepository
import app.thecarl.core.data.session.EnrolmentResult
import app.thecarl.core.data.session.LoginResult
import app.thecarl.core.data.session.SessionRepository
import app.thecarl.core.data.session.SessionState
import app.thecarl.core.domain.model.Provider
import app.thecarl.core.domain.model.TransactionType
import app.thecarl.core.domain.sync.OutboxState
import app.thecarl.ui.state.CaptureConfirmation
import app.thecarl.ui.state.CaptureError
import app.thecarl.ui.state.CaptureProvider
import app.thecarl.ui.state.CaptureTransactionType
import app.thecarl.ui.state.CaptureUiState
import app.thecarl.ui.state.DashboardUiState
import app.thecarl.ui.state.EnrolmentError
import app.thecarl.ui.state.EnrolmentUiState
import app.thecarl.ui.state.LoginError
import app.thecarl.ui.state.LoginUiState
import java.math.BigDecimal
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow

/**
 * Sign-in presentation logic.
 *
 * <p>Holds no credentials beyond the life of the form and performs no authentication itself
 * — [SessionRepository] remains the only writer to secure storage.</p>
 */
class LoginViewModel(private val sessionRepository: SessionRepository) {

    private val _state = MutableStateFlow(LoginUiState())
    val state: StateFlow<LoginUiState> = _state.asStateFlow()

    fun onEmailChanged(email: String) {
        _state.value = _state.value.copy(email = email, error = null)
    }

    fun onPasswordChanged(password: String) {
        _state.value = _state.value.copy(password = password, error = null)
    }

    /**
     * Attempts sign-in.
     *
     * <p>Guarded against re-entry: a double tap must not send two login requests, which
     * would consume the rate limit twice and could race on credential storage.</p>
     */
    suspend fun submit(): Boolean {
        val current = _state.value
        if (!current.canSubmit) return false

        _state.value = current.copy(isSubmitting = true, error = null)

        return when (val result = sessionRepository.login(current.email, current.password)) {
            is LoginResult.Success -> {
                // The password is dropped the moment it is no longer needed. It is never
                // written anywhere, and it does not linger in state behind a later screen.
                _state.value = LoginUiState(email = current.email)
                true
            }

            LoginResult.InvalidCredentials -> failWith(current, LoginError.INVALID_CREDENTIALS)
            is LoginResult.RateLimited -> failWith(current, LoginError.RATE_LIMITED)
            LoginResult.NetworkUnavailable -> failWith(current, LoginError.NETWORK_UNAVAILABLE)
            is LoginResult.ServerError -> failWith(current, LoginError.SERVER_ERROR)
        }
    }

    /** Clears the password on failure while keeping the email, so retyping is minimal. */
    private fun failWith(current: LoginUiState, error: LoginError): Boolean {
        _state.value = current.copy(password = "", isSubmitting = false, error = error)
        return false
    }
}

/**
 * Device enrolment presentation logic.
 *
 * <p>Collects a code and nothing else. Organization, branch, role, device type and
 * capabilities all come from the code the administrator issued — a form that asked for them
 * would let a handset enrol itself into the wrong branch.</p>
 */
class EnrolmentViewModel(private val sessionRepository: SessionRepository) {

    private val _state = MutableStateFlow(EnrolmentUiState())
    val state: StateFlow<EnrolmentUiState> = _state.asStateFlow()

    fun onCodeChanged(code: String) {
        _state.value = _state.value.copy(code = code, error = null)
    }

    suspend fun submit(): Boolean {
        val current = _state.value
        if (!current.canSubmit) return false

        _state.value = current.copy(isSubmitting = true, error = null)

        return when (sessionRepository.enrolDevice(current.code)) {
            is EnrolmentResult.Success -> {
                _state.value = EnrolmentUiState()
                true
            }

            EnrolmentResult.InvalidOrExpiredCode -> failWith(current, EnrolmentError.INVALID_OR_EXPIRED)
            EnrolmentResult.AlreadyEnrolled -> failWith(current, EnrolmentError.ALREADY_ENROLLED)
            EnrolmentResult.NetworkUnavailable -> failWith(current, EnrolmentError.NETWORK_UNAVAILABLE)
            is EnrolmentResult.ServerError -> failWith(current, EnrolmentError.SERVER_ERROR)
        }
    }

    private fun failWith(current: EnrolmentUiState, error: EnrolmentError): Boolean {
        _state.value = current.copy(isSubmitting = false, error = error)
        return false
    }
}

/**
 * Dashboard presentation logic.
 *
 * <p>Reads counts and totals from the local database. It computes no financial direction —
 * the stored deltas already carry it, having been decided by LedgerProjection at capture.</p>
 */
class DashboardViewModel(
    private val outboxRepository: OutboxRepository,
    private val sessionRepository: SessionRepository,
    private val localTotals: suspend () -> Pair<Long, Long>?,
    private val syncedTodayCount: suspend () -> Int
) {
    private val _state = MutableStateFlow(DashboardUiState())
    val state: StateFlow<DashboardUiState> = _state.asStateFlow()

    suspend fun refresh(isOnline: Boolean = true) {
        val device = (sessionRepository.state.value as? SessionState.Active)?.device
        val totals = runCatching { localTotals() }.getOrNull()

        _state.value = DashboardUiState(
            device = device,
            pendingCount = outboxRepository.countByState(OutboxState.PENDING),
            syncingCount = outboxRepository.countByState(OutboxState.SYNCING),
            retryingCount = outboxRepository.countByState(OutboxState.RETRYABLE_FAILURE),
            conflictCount = outboxRepository.countByState(OutboxState.CONFLICT),
            deadLetterCount = outboxRepository.countByState(OutboxState.DEAD_LETTER),
            syncedTodayCount = runCatching { syncedTodayCount() }.getOrDefault(0),
            // Absent rather than zero when unavailable: a fabricated total on a financial
            // dashboard is worse than an empty one.
            todayCashMinor = totals?.first,
            todayFloatMinor = totals?.second,
            isOnline = isOnline,
            isSyncing = outboxRepository.countByState(OutboxState.SYNCING) > 0
        )
    }
}

/**
 * Manual capture presentation logic.
 *
 * <p>Validates shape and hands everything else to [CaptureRepository]. It does not decide
 * ledger direction, compute a fingerprint, generate an identity, or touch a balance — all of
 * which belong to the domain and are already tested there.</p>
 */
class CaptureViewModel(
    private val captureRepositoryProvider: () -> TransactionCapture?,
    private val now: () -> Long = System::currentTimeMillis
) {
    private val _state = MutableStateFlow(CaptureUiState())
    val state: StateFlow<CaptureUiState> = _state.asStateFlow()

    fun onTypeChanged(type: CaptureTransactionType) {
        _state.value = _state.value.copy(transactionType = type, error = null)
    }

    fun onAmountChanged(amount: String) {
        // Stored raw and parsed once at submission. Parsing per keystroke would reject
        // partial input such as "12." or silently reinterpret it.
        _state.value = _state.value.copy(amountInput = amount, error = null)
    }

    fun onProviderChanged(provider: CaptureProvider) {
        _state.value = _state.value.copy(provider = provider, error = null)
    }

    fun onCustomerPhoneChanged(value: String) {
        _state.value = _state.value.copy(customerPhone = value, error = null)
    }

    fun onReferenceChanged(value: String) {
        _state.value = _state.value.copy(reference = value, error = null)
    }

    fun onNotesChanged(value: String) {
        _state.value = _state.value.copy(notes = value, error = null)
    }

    fun dismissConfirmation() {
        _state.value = _state.value.copy(lastResult = null)
    }

    /**
     * Records the transaction locally and queues it.
     *
     * <p>Returns true when it is safely on this device — <b>not</b> when the server has
     * accepted it. Those are different facts and the confirmation says so.</p>
     */
    suspend fun submit(isOnline: Boolean = true): Boolean {
        val current = _state.value
        if (!current.canSubmit) return false

        val amount = parseAmount(current.amountInput)
            ?: return fail(current, CaptureError.INVALID_AMOUNT)

        if (amount <= BigDecimal.ZERO) {
            return fail(current, CaptureError.AMOUNT_TOO_SMALL)
        }

        // Rejected rather than rounded. A rounded amount is a wrong amount, and an agent
        // would have no way to know it happened.
        val amountMinor = runCatching { MinorUnits.fromDecimal(amount) }.getOrNull()
            ?: return fail(current, CaptureError.SUB_PESEWA_PRECISION)

        val repository = captureRepositoryProvider()
            ?: return fail(current, CaptureError.NO_ACTIVE_DEVICE)

        _state.value = current.copy(isSubmitting = true, error = null)

        val outcome = runCatching {
            repository.captureManual(
                ManualCaptureRequest(
                    transactionType = current.transactionType.toDomain(),
                    amount = amount,
                    provider = current.provider.toDomain(),
                    occurredAtUtcMillis = now(),
                    reference = current.reference.ifBlank { null },
                    customerPhoneNumber = current.customerPhone.ifBlank { null },
                    notes = current.notes.ifBlank { null }
                )
            )
        }.getOrElse {
            // Local persistence failed. This is the one case that genuinely lost the
            // transaction, and it must never be confused with a network problem.
            return fail(current, CaptureError.LOCAL_SAVE_FAILED)
        }

        return when (outcome) {
            is CaptureOutcome.Queued -> {
                _state.value = CaptureUiState(
                    transactionType = current.transactionType,
                    provider = current.provider,
                    lastResult = CaptureConfirmation(
                        clientTransactionId = outcome.clientTransactionId,
                        transactionType = current.transactionType,
                        amountMinor = amountMinor,
                        isQueued = true,
                        isOnline = isOnline
                    )
                )
                true
            }

            is CaptureOutcome.HeldForReview -> {
                _state.value = current.copy(
                    isSubmitting = false,
                    error = CaptureError.HELD_FOR_REVIEW
                )
                false
            }

            is CaptureOutcome.DuplicateOnThisDevice -> fail(current, CaptureError.DUPLICATE_ON_DEVICE)
            is CaptureOutcome.Rejected -> fail(current, CaptureError.AMOUNT_TOO_SMALL)

            // Only an SMS can be ignored — a manual entry always describes a transaction.
            // Reported as a rejection rather than silently succeeding.
            is CaptureOutcome.Ignored -> fail(current, CaptureError.AMOUNT_TOO_SMALL)
        }
    }

    /** Strict: anything not a plain decimal is refused rather than coerced. */
    private fun parseAmount(input: String): BigDecimal? =
        runCatching { BigDecimal(input.trim().replace(",", "")) }.getOrNull()

    private fun fail(current: CaptureUiState, error: CaptureError): Boolean {
        _state.value = current.copy(isSubmitting = false, error = error)
        return false
    }

    private fun CaptureTransactionType.toDomain() = when (this) {
        CaptureTransactionType.CASH_IN -> TransactionType.CASH_IN
        CaptureTransactionType.CASH_OUT -> TransactionType.CASH_OUT
        CaptureTransactionType.TRANSFER -> TransactionType.TRANSFER
        CaptureTransactionType.COMMISSION -> TransactionType.COMMISSION
    }

    private fun CaptureProvider.toDomain() = when (this) {
        CaptureProvider.MTN -> Provider.MTN
        CaptureProvider.TELECEL -> Provider.TELECEL
        CaptureProvider.AIRTELTIGO -> Provider.AIRTELTIGO
    }
}
