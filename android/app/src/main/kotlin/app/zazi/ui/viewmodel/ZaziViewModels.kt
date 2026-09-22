package app.zazi.ui.viewmodel

import app.zazi.core.domain.model.GhanaPhoneNumber
import app.zazi.core.data.capture.CaptureOutcome
import app.zazi.core.data.capture.ManualCaptureRequest
import app.zazi.core.data.capture.MinorUnits
import app.zazi.core.data.repository.TransactionCapture
import app.zazi.core.data.repository.OutboxRepository
import app.zazi.core.data.session.ActivationResult
import app.zazi.core.data.session.EnrolmentResult
import app.zazi.core.data.session.LoginResult
import app.zazi.core.data.session.SessionRepository
import app.zazi.core.data.session.SessionState
import app.zazi.core.domain.model.Provider
import app.zazi.core.domain.model.TransactionType
import app.zazi.core.domain.sync.OutboxState
import app.zazi.ui.state.ActivationError
import app.zazi.ui.state.ActivationUiState
import app.zazi.ui.state.ActivityItem
import app.zazi.ui.state.ActivityFilter
import app.zazi.ui.state.TransactionDetail
import app.zazi.ui.state.CaptureConfirmation
import app.zazi.ui.state.CaptureError
import app.zazi.ui.state.CaptureProvider
import app.zazi.ui.state.CaptureTransactionType
import app.zazi.ui.state.CaptureUiState
import app.zazi.ui.state.DashboardUiState
import app.zazi.ui.state.HoldingsUiState
import app.zazi.ui.state.EnrolmentError
import app.zazi.ui.state.EnrolmentUiState
import app.zazi.ui.state.LoginError
import app.zazi.ui.state.LoginUiState
import java.math.BigDecimal
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.emptyFlow
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
 * First-run activation.
 *
 * <p>Holds no identity of its own. Everything shown after a successful activation — the
 * worker's name, their branch, their business — comes from the server's response, because the
 * handset has no way to know any of it and should not invent it.</p>
 */
class ActivationViewModel(private val sessionRepository: SessionRepository) {

    private val _state = MutableStateFlow(ActivationUiState())
    val state: StateFlow<ActivationUiState> = _state.asStateFlow()

    fun onCodeChanged(code: String) {
        _state.value = _state.value.copy(code = code, error = null)
    }

    suspend fun submit(): Boolean {
        val current = _state.value
        if (!current.canSubmit) return false

        _state.value = current.copy(isSubmitting = true, error = null)

        return when (val result = sessionRepository.activate(current.code)) {
            is ActivationResult.Success -> {
                // The code is dropped from state the moment it is spent. It is single use and
                // there is no reason for it to survive in memory.
                _state.value = ActivationUiState(activated = result.identity)
                true
            }

            ActivationResult.CodeNotValid -> failWith(current, ActivationError.CODE_NOT_VALID)
            ActivationResult.AlreadyActivated -> failWith(current, ActivationError.ALREADY_ACTIVATED)
            is ActivationResult.RateLimited -> failWith(current, ActivationError.RATE_LIMITED)
            ActivationResult.NetworkUnavailable -> failWith(current, ActivationError.NETWORK_UNAVAILABLE)
            is ActivationResult.ServerError -> failWith(current, ActivationError.SERVER_ERROR)
        }
    }

    private fun failWith(current: ActivationUiState, error: ActivationError): Boolean {
        // The code is kept on a failure, unlike on success: a mistyped character is the most
        // likely cause and clearing the field would make the worker type all of it again.
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
    private val syncedTodayCount: suspend () -> Int,
    /**
     * What this device has recorded. Supplied as a function so this class stays free of
     * Room, and defaulted to empty so existing callers and tests are unaffected.
     */
    private val recentActivity: suspend (ActivityFilter) -> List<ActivityItem> = { emptyList() },
    /** One customer's transactions, across every date. Defaulted so existing tests are unaffected. */
    private val searchActivity: suspend (String) -> List<ActivityItem> = { emptyList() },
    /** Detail for one transaction, or null if it has gone. */
    private val transactionDetail: suspend (String) -> TransactionDetail? = { null },
    /**
     * The same detail as a stream, for a screen that stays open while the row changes.
     * Defaulted to empty so existing callers and tests are unaffected.
     */
    private val transactionDetailStream: (String) -> Flow<TransactionDetail?> = { emptyFlow() },
    /** Returns whether the item was actually re-queued. */
    private val retryTransaction: suspend (String) -> Boolean = { false },
    /**
     * What the owner has given this agent, from the server. Defaulted to nothing so existing
     * callers and tests are unaffected, and absent rather than zero when it cannot be read.
     */
    private val holdings: suspend () -> HoldingsUiState? = { null }
) {
    private val _state = MutableStateFlow(DashboardUiState())
    val state: StateFlow<DashboardUiState> = _state.asStateFlow()

    /** The slice of history on screen. Held here so a refresh does not reset the agent's choice. */
    private var filter: ActivityFilter = ActivityFilter.TODAY

    /** A customer number being looked up; blank means "show the chosen day". */
    private var query: String = ""

    suspend fun onFilterChanged(value: ActivityFilter, isOnline: Boolean) {
        filter = value
        refresh(isOnline)
    }

    suspend fun onSearchChanged(value: String, isOnline: Boolean) {
        query = value
        refresh(isOnline)
    }

    /**
     * Detail for one transaction.
     *
     * <p>Read on demand rather than carried in the list: the list is redrawn constantly as
     * sync progresses, and it has no business holding customer numbers it does not display.</p>
     */
    suspend fun detailFor(clientTransactionId: String): TransactionDetail? =
        runCatching { transactionDetail(clientTransactionId) }.getOrNull()

    /**
     * Follows one transaction while a screen is showing it.
     *
     * <p>The detail screen used to hold the snapshot it was opened with, so an item retried
     * from that screen stayed on "Sending" after the sync engine had delivered it — the row
     * had moved on and nothing re-read it. This observes the same persisted row the list
     * reads, so the screen follows the record rather than a copy of it.</p>
     */
    fun observeDetail(clientTransactionId: String): Flow<TransactionDetail?> =
        transactionDetailStream(clientTransactionId)

    /**
     * Puts a stopped transaction back in the queue at the agent's request.
     *
     * <p>Returns false when nothing changed, which is not necessarily a failure — a retry may
     * have delivered it in the meantime. The caller re-reads rather than assuming either way.</p>
     */
    suspend fun retry(clientTransactionId: String, isOnline: Boolean): Boolean {
        val requeued = runCatching { retryTransaction(clientTransactionId) }.getOrDefault(false)
        refresh(isOnline)
        return requeued
    }

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
            // Failing to read the history must not blank the figures above it; an empty list
            // is the honest fallback, and the screen says when there is nothing to show.
            activity = runCatching {
                if (query.isNotBlank()) searchActivity(query) else recentActivity(filter)
            }.getOrDefault(emptyList()),
            activityFilter = filter,
            searchQuery = query,
            isOnline = isOnline,
            // Absent rather than zero when the server cannot be reached: a made-up holding is
            // worse than none, and the agent knows what "—" means.
            holdings = runCatching { holdings() }.getOrNull(),
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

        // Normalised before it is stored, so "0244 123 456", "233244123456" and
        // "+233 24 412 3456" are one customer and a search for any of them finds the others.
        val customer: String? = when {
            current.customerPhone.isBlank() ->
                if (current.transactionType.requiresCustomer) {
                    return fail(current, CaptureError.MISSING_CUSTOMER_NUMBER)
                } else {
                    null
                }
            else -> GhanaPhoneNumber.normalise(current.customerPhone)
                ?: return fail(current, CaptureError.INVALID_CUSTOMER_NUMBER)
        }

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
                    customerPhoneNumber = customer,
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
