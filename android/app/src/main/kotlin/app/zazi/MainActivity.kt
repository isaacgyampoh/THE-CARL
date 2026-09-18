package app.zazi

import android.Manifest
import android.content.Context
import android.content.pm.PackageManager
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.ui.platform.LocalContext
import androidx.core.content.ContextCompat
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.material3.Surface
import androidx.activity.compose.BackHandler
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.systemBarsPadding
import androidx.compose.ui.Modifier
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import app.zazi.core.data.database.TransactionDetailRow
import app.zazi.core.data.session.SessionState
import app.zazi.core.data.sync.SyncWorker
import app.zazi.ui.CaptureScreen
import app.zazi.ui.DashboardScreen
import app.zazi.ui.DeviceRevokedScreen
import app.zazi.ui.EnrolmentScreen
import app.zazi.ui.LoadingScreen
import app.zazi.ui.LoginScreen
import app.zazi.ui.TransactionDetailScreen
import app.zazi.ui.state.ActivityDelivery
import app.zazi.ui.state.TransactionDetail
import app.zazi.ui.state.ActivityItem
import app.zazi.ui.state.CaptureTransactionType
import app.zazi.ui.viewmodel.CaptureViewModel
import app.zazi.ui.viewmodel.DashboardViewModel
import app.zazi.ui.viewmodel.EnrolmentViewModel
import app.zazi.ui.theme.ZaziTheme
import app.zazi.ui.viewmodel.LoginViewModel
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.flowOf
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.launch

/**
 * Single activity hosting the whole flow.
 *
 * <p>Navigation is derived from [SessionState] rather than tracked separately. The session
 * repository already knows whether the user is signed in, enrolled or revoked, and a second
 * copy of that knowledge in the UI would eventually disagree with it.</p>
 */
class MainActivity : ComponentActivity() {

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        val container = (application as ZaziApplication).container

        setContent {
            ZaziTheme {
                // targetSdk 35 draws edge to edge, so without this every screen's header
                // sits underneath the status bar clock. Applied once at the root rather
                // than per screen, so a new screen cannot forget it.
                //
                // imePadding for the same reason: the activity is adjustResize, but nothing
                // was insetting for the keyboard, so on a short handset it covered whatever
                // was at the bottom of the screen — including the sign-in button.
                Surface(
                    modifier = Modifier
                        .fillMaxSize()
                        .systemBarsPadding()
                        .imePadding()
                ) {
                    ZaziApp(container, application as ZaziApplication)
                }
            }
        }
    }
}

/** Screen currently shown within the authenticated part of the app. */
private enum class AuthenticatedScreen { DASHBOARD, CAPTURE, TRANSACTION }

@Composable
private fun ZaziApp(container: AppContainer, application: ZaziApplication) {
    val scope = rememberCoroutineScope()
    val sessionState by container.sessionRepository.state.collectAsStateWithLifecycle()

    val loginViewModel = remember { LoginViewModel(container.sessionRepository) }
    val enrolmentViewModel = remember { EnrolmentViewModel(container.sessionRepository) }

    val captureViewModel = remember {
        CaptureViewModel(captureRepositoryProvider = { container.captureRepository() })
    }

    val dashboardViewModel = remember {
        DashboardViewModel(
            outboxRepository = container.outboxRepository,
            sessionRepository = container.sessionRepository,
            localTotals = {
                // Business-day window, summed from stored deltas. Direction was decided at
                // capture by LedgerProjection; this only adds up.
                val dayStart = startOfDayUtcMillis()
                val totals = container.dashboardRepository
                    .totalsBetween(dayStart, dayStart + DAY_MILLIS)
                totals.cashMinor to totals.floatMinor
            },
            syncedTodayCount = { container.dashboardRepository.syncedCount() },
            recentActivity = { filter ->
                // Mapped here rather than in the repository so the persistence projection
                // stays a persistence concern and the screen gets a model in its own terms.
                val window = filter.windowUtcMillis(System.currentTimeMillis())
                container.dashboardRepository
                    .observeBetween(window.first, window.last + 1)
                    .first()
                    .map { row ->
                    ActivityItem(
                        clientTransactionId = row.clientTransactionId,
                        label = CaptureTransactionType.entries
                            .firstOrNull { it.name == row.transactionType }
                            ?.label
                        // Not every stored type is offerable on the capture form — a reversal
                        // or an adjustment can arrive from elsewhere — so an unknown type is
                        // shown readably rather than dropped from the agent's own history.
                            ?: row.transactionType.lowercase().replace('_', ' ')
                                .replaceFirstChar { it.uppercase() },
                        provider = row.provider,
                        amountMinor = row.amountMinor,
                        cashDeltaMinor = row.cashDeltaMinor,
                        atUtcMillis = row.transactionAtUtcMillis,
                        capturedAutomatically = row.sourceType == "SMS",
                        delivery = ActivityDelivery.fromOutboxState(row.outboxState)
                    )
                }
            },
            transactionDetail = { clientTransactionId ->
                container.dashboardRepository.findDetail(clientTransactionId)?.toDetail()
            },
            // The same row, observed. A screen left open follows the record as sync moves it
            // instead of holding the snapshot it was opened with.
            transactionDetailStream = { clientTransactionId ->
                container.dashboardRepository
                    .observeDetail(clientTransactionId)
                    .map { row -> row?.toDetail() }
            },
            retryTransaction = { clientTransactionId ->
                val requeued = container.dashboardRepository
                    .retryDeadLettered(clientTransactionId, System.currentTimeMillis())
                // Only wake the worker if something actually changed; an unnecessary wake on
                // every tap would drain a handset that is already struggling to sync.
                if (requeued) SyncWorker.enqueue(application)
                requeued
            }
        )
    }

    var screen by remember { mutableStateOf(AuthenticatedScreen.DASHBOARD) }
    // The id is what the screen owns; the detail itself comes from the database so it stays
    // current. openedWith is the snapshot the row was tapped with, used only as the stream's
    // initial value so the screen never flashes empty before Room's first emission.
    var selectedTransactionId by remember { mutableStateOf<String?>(null) }
    var openedWith by remember { mutableStateOf<TransactionDetail?>(null) }
    var isRetrying by remember { mutableStateOf(false) }

    when (val state = sessionState) {
        SessionState.Initialising -> LoadingScreen()

        SessionState.SignedOut -> {
            val loginState by loginViewModel.state.collectAsState()

            LoginScreen(
                state = loginState,
                onEmailChanged = loginViewModel::onEmailChanged,
                onPasswordChanged = loginViewModel::onPasswordChanged,
                onSubmit = {
                    scope.launch {
                        if (loginViewModel.submit()) {
                            // Drain anything captured before this sign-in.
                            SyncWorker.enqueue(application)
                        }
                    }
                }
            )
        }

        is SessionState.NeedsEnrolment -> {
            val enrolmentState by enrolmentViewModel.state.collectAsState()

            EnrolmentScreen(
                state = enrolmentState,
                onCodeChanged = enrolmentViewModel::onCodeChanged,
                onSubmit = {
                    scope.launch {
                        if (enrolmentViewModel.submit()) {
                            SyncWorker.enqueue(application)
                        }
                    }
                }
            )
        }

        is SessionState.DeviceRevoked -> DeviceRevokedScreen(
            queuedWorkCount = state.queuedWorkCount,
            onSignIn = { scope.launch { container.sessionRepository.logout() } }
        )

        is SessionState.Active -> {
            // Starts optimistic so the first frame does not flash "Offline" before the
            // platform answers; the observer corrects it immediately.
            val isOnline by container.connectivityObserver.isOnline
                .collectAsState(initial = true)

            LaunchedEffect(screen, isOnline) { dashboardViewModel.refresh(isOnline) }

            when (screen) {
                AuthenticatedScreen.DASHBOARD -> {
                    val dashboardState by dashboardViewModel.state.collectAsState()
                    val context = LocalContext.current

                    var smsPermissionGranted by remember {
                        mutableStateOf(context.hasSmsPermission())
                    }

                    val permissionLauncher = rememberLauncherForActivityResult(
                        ActivityResultContracts.RequestPermission()
                    ) { granted ->
                        // A refusal is a legitimate answer, not a failure. Manual capture
                        // continues to work and nothing queued is affected.
                        smsPermissionGranted = granted
                    }

                    DashboardScreen(
                        state = dashboardState,
                        onCapture = { screen = AuthenticatedScreen.CAPTURE },
                        onFilterChanged = { filter ->
                            scope.launch { dashboardViewModel.onFilterChanged(filter, isOnline) }
                        },
                        onActivitySelected = { item ->
                            scope.launch {
                                // Read before navigating, so the screen never appears empty
                                // and then fills in.
                                openedWith =
                                    dashboardViewModel.detailFor(item.clientTransactionId)
                                selectedTransactionId = item.clientTransactionId
                                screen = AuthenticatedScreen.TRANSACTION
                            }
                        },
                        // A trigger only. The outbox remains the source of truth and the UI
                        // never calls the sync API directly.
                        onSyncNow = { SyncWorker.enqueue(application) },
                        onLogout = { scope.launch { container.sessionRepository.logout() } },
                        smsPermissionGranted = smsPermissionGranted,
                        // Asked for only when the agent taps, never on launch: a permission
                        // prompt before any explanation is how people learn to decline.
                        onRequestSmsPermission = {
                            permissionLauncher.launch(Manifest.permission.RECEIVE_SMS)
                        }
                    )
                }

                AuthenticatedScreen.TRANSACTION -> {
                    val transactionId = selectedTransactionId

                    // Follows the stored row for as long as this screen is on top. Collected
                    // with lifecycle awareness so it stops while the app is backgrounded
                    // rather than keeping a query alive behind a locked phone. Room emits on
                    // writes to either joined table, so a retry re-queueing the outbox row
                    // and the engine later marking it synced both arrive here — no polling,
                    // and no request of its own.
                    val detail by remember(transactionId) {
                        if (transactionId == null) {
                            flowOf(null)
                        } else {
                            dashboardViewModel.observeDetail(transactionId)
                        }
                    }.collectAsStateWithLifecycle(initialValue = openedWith)

                    fun leave() {
                        selectedTransactionId = null
                        openedWith = null
                        screen = AuthenticatedScreen.DASHBOARD
                    }

                    // Back returns to the list rather than leaving the app, for the same
                    // reason capture does: an agent reaching for "go back" after checking a
                    // figure should land where they came from.
                    BackHandler { leave() }

                    TransactionDetailScreen(
                        detail = detail,
                        isRetrying = isRetrying,
                        onRetry = {
                            val target = transactionId ?: return@TransactionDetailScreen
                            scope.launch {
                                isRetrying = true
                                // The re-read that used to follow this is gone: the stream
                                // above reports what is now true, including a retry refused
                                // because the item had already been delivered.
                                dashboardViewModel.retry(target, isOnline)
                                isRetrying = false
                            }
                        },
                        onBack = { leave() }
                    )
                }

                AuthenticatedScreen.CAPTURE -> {
                    val captureState by captureViewModel.state.collectAsState()

                    // Without this, back from capture leaves the application entirely rather
                    // than returning to the dashboard — an agent reaching for "go back" after
                    // recording a transaction would be dropped onto the home screen.
                    BackHandler {
                        captureViewModel.dismissConfirmation()
                        screen = AuthenticatedScreen.DASHBOARD
                    }

                    CaptureScreen(
                        state = captureState,
                        onTypeChanged = captureViewModel::onTypeChanged,
                        onProviderChanged = captureViewModel::onProviderChanged,
                        onAmountChanged = captureViewModel::onAmountChanged,
                        onCustomerPhoneChanged = captureViewModel::onCustomerPhoneChanged,
                        onReferenceChanged = captureViewModel::onReferenceChanged,
                        onSubmit = {
                            scope.launch {
                                if (captureViewModel.submit(isOnline)) {
                                    // Best-effort. The transaction is already committed to
                                    // the outbox, so a failed enqueue cannot lose it.
                                    application.scheduleSync()
                                    dashboardViewModel.refresh(isOnline)
                                }
                            }
                        },
                        onDone = {
                            captureViewModel.dismissConfirmation()
                            screen = AuthenticatedScreen.DASHBOARD
                        }
                    )
                }
            }
        }
    }
}

/**
 * Presentation model for one transaction.
 *
 * <p>One mapping, used by both the read that opens the screen and the stream that keeps it
 * current, so the two cannot disagree about what a row means.</p>
 */
private fun TransactionDetailRow.toDetail(): TransactionDetail {
    val state = outboxState

    return TransactionDetail(
        clientTransactionId = transaction.clientTransactionId,
        label = CaptureTransactionType.entries
            .firstOrNull { it.name == transaction.transactionType }
            ?.label
            ?: transaction.transactionType.lowercase().replace('_', ' ')
                .replaceFirstChar { it.uppercase() },
        provider = transaction.provider,
        amountMinor = transaction.amountMinor,
        cashDeltaMinor = transaction.cashDeltaMinor,
        atUtcMillis = transaction.transactionAtUtcMillis,
        customerPhone = transaction.customerPhoneNumber,
        reference = transaction.reference,
        capturedAutomatically = transaction.sourceType == "SMS",
        delivery = ActivityDelivery.fromOutboxState(state),
        attemptCount = attemptCount ?: 0,
        lastReasonCode = lastReasonCode,
        // Only a dead letter. A conflict means the server disagreed, which re-sending cannot
        // resolve — the DAO enforces the same boundary, so this decides what to offer rather
        // than what is permitted.
        isRetryable = state == "DEAD_LETTER"
    )
}

private const val DAY_MILLIS = 24 * 60 * 60 * 1000L

/** Start of the current day in UTC. Ghana observes UTC+0, so this is the business day. */
private fun startOfDayUtcMillis(): Long =
    System.currentTimeMillis() / DAY_MILLIS * DAY_MILLIS

/**
 * Whether this installation may currently receive SMS.
 *
 * <p>Runtime state only. It says nothing about whether the server permits this device to
 * capture SMS — that remains {@code DeviceContext.canAttemptSmsCapture}, and both must hold
 * before a message can become evidence.</p>
 */
private fun Context.hasSmsPermission(): Boolean =
    ContextCompat.checkSelfPermission(this, Manifest.permission.RECEIVE_SMS) ==
        PackageManager.PERMISSION_GRANTED
