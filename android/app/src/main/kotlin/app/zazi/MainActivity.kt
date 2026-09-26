package app.zazi

import app.zazi.core.data.repository.StatementDownload
import app.zazi.core.data.repository.DayCloseOutcome
import app.zazi.core.data.repository.FloatRequestOutcome
import app.zazi.core.data.repository.HeldMessage
import app.zazi.core.data.network.FloatRequestInfo
import app.zazi.ui.FloatRequestDialog
import app.zazi.core.data.network.DayCloseResponse
import app.zazi.sms.InboxBackfill
import app.zazi.ui.CloseDayScreen
import app.zazi.ui.HeldMessagesScreen
import androidx.core.content.FileProvider
import android.content.Intent
import app.zazi.core.data.database.RecentTransactionRow
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
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.systemBarsPadding
import app.zazi.ui.theme.LocalStatusBarGround
import androidx.compose.ui.graphics.Color
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.windowInsetsTopHeight
import androidx.compose.foundation.layout.statusBars
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.ui.Modifier
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.listSaver
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import app.zazi.core.data.repository.ReportOutcome
import app.zazi.ui.state.ParsingReportUiState
import app.zazi.core.data.database.TransactionDetailRow
import app.zazi.core.data.session.SessionState
import app.zazi.core.data.sync.SyncWorker
import app.zazi.ui.brand.BrandIntroGate
import app.zazi.ui.brand.LaunchState
import app.zazi.ui.brand.ZaziBrandIntro
import app.zazi.ui.design.rememberReducedMotion
import app.zazi.ui.ActivatedScreen
import app.zazi.ui.ActivationScreen
import app.zazi.ui.CaptureScreen
import app.zazi.ui.DataLostNotice
import app.zazi.ui.DashboardScreen
import app.zazi.ui.DeviceRevokedScreen
import app.zazi.ui.EnrolmentScreen
import app.zazi.ui.LoadingScreen
import app.zazi.ui.LoginScreen
import app.zazi.ui.TransactionDetailScreen
import app.zazi.ui.state.EntryDestination
import app.zazi.ui.state.EntryNavigator
import app.zazi.ui.state.ActivityDelivery
import app.zazi.ui.state.TransactionDetail
import app.zazi.ui.state.ActivityItem
import app.zazi.ui.state.Receipt
import app.zazi.ui.state.CloseDay
import app.zazi.ui.state.NetworkHolding
import app.zazi.core.domain.ledger.MissingTransactions
import app.zazi.ui.state.HeldMessageUiItem
import app.zazi.ui.state.MissingMessage
import app.zazi.ui.state.HoldingsUiState
import app.zazi.ui.state.RemoteActivity
import app.zazi.core.data.network.RemoteTransaction
import app.zazi.ui.state.CaptureTransactionType
import app.zazi.ui.viewmodel.ActivationViewModel
import app.zazi.ui.viewmodel.CaptureViewModel
import app.zazi.ui.viewmodel.DashboardViewModel
import app.zazi.ui.viewmodel.EnrolmentViewModel
import app.zazi.ui.theme.ZaziTheme
import app.zazi.ui.viewmodel.LoginViewModel
import java.time.Instant
import java.time.ZoneId
import java.time.format.DateTimeFormatter
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
                // The introduction overlays the app rather than replacing it, so the real
                // work of starting up — restoring the session, opening the database, the
                // first screen composing — happens underneath it and is already finished
                // when it lifts. Branching instead would have made the brand cost the user
                // time rather than occupy time they were spending anyway.
                var statusGround by remember { mutableStateOf<Color?>(null) }

                Box(modifier = Modifier.fillMaxSize()) {
                    // The strip behind the status bar, coloured by whichever screen asks for it.
                    statusGround?.let { ground ->
                        Box(
                            Modifier
                                .fillMaxWidth()
                                .windowInsetsTopHeight(WindowInsets.statusBars)
                                .background(ground)
                        )
                    }
                    CompositionLocalProvider(LocalStatusBarGround provides { statusGround = it }) {
                    Surface(
                        modifier = Modifier
                            .fillMaxSize()
                            .systemBarsPadding()
                            .imePadding()
                    ) {
                        ZaziApp(container, application as ZaziApplication)
                    }
                    }

                    val sessionState by container.sessionRepository.state
                        .collectAsStateWithLifecycle()

                    // Survives configuration change, which is what stops a rotation during
                    // launch — or the system recreating the activity — from replaying the
                    // animation at somebody who has already watched it.
                    var launch by rememberSaveable(stateSaver = LaunchStateSaver) {
                        mutableStateOf(LaunchState())
                    }

                    LaunchedEffect(sessionState) {
                        launch = BrandIntroGate.onSessionResolved(
                            launch,
                            sessionState !is SessionState.Initialising
                        )
                    }

                    if (BrandIntroGate.isVisible(launch)) {
                        // Drawn outside the system-bar padding on purpose: the introduction
                        // is full bleed, continuous with the system splash it takes over
                        // from, so a strip of app background at the top would give away the
                        // handover.
                        ZaziBrandIntro(
                            reducedMotion = rememberReducedMotion(),
                            onFinished = {
                                launch = BrandIntroGate.onAnimationFinished(launch)
                            }
                        )
                    }
                }
            }
        }
    }
}

/**
 * Persists the launch gate across configuration change.
 *
 * <p>Only the three flags matter, and they are booleans, so a list saver is enough — the
 * alternative, making [LaunchState] parcelable, would put an Android type into a class whose
 * whole point is being testable without one.</p>
 */
private val LaunchStateSaver = listSaver<LaunchState, Boolean>(
    save = { listOf(it.animationFinished, it.sessionResolved, it.introCompleted) },
    restore = { LaunchState(it[0], it[1], it[2]) }
)

/** Screen currently shown within the authenticated part of the app. */
private enum class AuthenticatedScreen { DASHBOARD, CAPTURE, TRANSACTION, CLOSE_DAY, HELD }

@Composable
private fun ZaziApp(container: AppContainer, application: ZaziApplication) {
    val scope = rememberCoroutineScope()
    val sessionState by container.sessionRepository.state.collectAsStateWithLifecycle()

    val loginViewModel = remember { LoginViewModel(container.sessionRepository) }
    val activationViewModel = remember { ActivationViewModel(container.sessionRepository) }

    // Whether the worker asked for the email form instead. Not a session state — nothing on
    // the server changes — so it lives here rather than in SessionState, which stays the
    // single description of what the server believes about this handset.
    var showLogin by rememberSaveable { mutableStateOf(false) }

    // Whether the worker has dismissed the confirmation screen. Activation moves SessionState
    // straight to Active, so without this the "You're connected" screen is unreachable: the
    // session exists before the worker has been told whose it is. Not saved across process
    // death on purpose — a worker returning to an already-activated app should land in the
    // workspace, not be congratulated again.
    var activationAcknowledged by remember { mutableStateOf(false) }
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
                // Plus what the agent recorded elsewhere today — a keypad phone, a second
                // handset — so the figure is the agent's day, not this handset's.
                val (elsewhereCash, elsewhereFloat) = RemoteActivity.totalsMinor(
                    recordedElsewhere(container, container.remoteActivityRepository.between(dayStart, dayStart + DAY_MILLIS))
                )
                (totals.cashMinor + elsewhereCash) to (totals.floatMinor + elsewhereFloat)
            },
            syncedTodayCount = {
                val dayStart = startOfDayUtcMillis()
                container.dashboardRepository.syncedCount(dayStart, dayStart + DAY_MILLIS)
            },
            recentActivity = { filter ->
                // Mapped here rather than in the repository so the persistence projection
                // stays a persistence concern and the screen gets a model in its own terms.
                val window = filter.windowUtcMillis(System.currentTimeMillis())
                val local = container.dashboardRepository
                    .observeBetween(window.first, window.last + 1)
                    .first()
                    .map { row -> row.toActivityItem() }
                val elsewhere = recordedElsewhere(
                    container,
                    container.remoteActivityRepository.between(window.first, window.last + 1)
                ).mapNotNull(RemoteActivity::toActivityItem)
                RemoteActivity.merge(local, elsewhere)
            },
            searchActivity = { query ->
                val local = container.dashboardRepository.searchByCustomer(query).map { row -> row.toActivityItem() }
                // A customer's complaint may be about a transaction done on the keypad phone.
                val elsewhere = recordedElsewhere(
                    container,
                    container.remoteActivityRepository.forCustomer(query)
                ).mapNotNull(RemoteActivity::toActivityItem)
                RemoteActivity.merge(local, elsewhere)
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
            heldCount = { container.dashboardRepository.observeHeldCount().first() },
            holdings = {
                container.balancesRepository.mine()?.let { balances ->
                    HoldingsUiState(
                        cashMinor = CloseDay.minor(balances.cash),
                        floatMinor = CloseDay.minor(balances.totalFloat),
                        networks = balances.floats.map { NetworkHolding(it.network, CloseDay.minor(it.amount)) },
                        cashGivenTodayMinor = CloseDay.minor(balances.cashGivenToday),
                        floatGivenTodayMinor = CloseDay.minor(balances.floatGivenToday)
                    )
                }
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
    // The held message a capture was started from, settled only once that capture is saved.
    var recordingHeldEvidenceId by remember { mutableStateOf<String?>(null) }
    var openedWith by remember { mutableStateOf<TransactionDetail?>(null) }
    var isRetrying by remember { mutableStateOf(false) }
    var statementBusy by remember { mutableStateOf(false) }
    var statementError by remember { mutableStateOf<String?>(null) }
    var closeBusy by remember { mutableStateOf(false) }
    var closeError by remember { mutableStateOf<String?>(null) }
    var closeResult by remember { mutableStateOf<DayCloseResponse?>(null) }
    var floatOpen by remember { mutableStateOf(false) }
    var floatBusy by remember { mutableStateOf(false) }
    var floatError by remember { mutableStateOf<String?>(null) }
    var floatSent by remember { mutableStateOf<FloatRequestInfo?>(null) }
    var floatRecent by remember { mutableStateOf<List<FloatRequestInfo>>(emptyList()) }

    val activationState by activationViewModel.state.collectAsState()
    val justActivated = activationState.activated

    // Read once per process. Local data that could not be decrypted is gone, and what was in
    // it was the agent's own unsynced captures — so this is said plainly rather than left for
    // them to notice as missing work.
    var orphanedAt by rememberSaveable { mutableStateOf<Long?>(null) }
    var orphanNoticeChecked by rememberSaveable { mutableStateOf(false) }

    LaunchedEffect(Unit) {
        if (!orphanNoticeChecked) {
            orphanNoticeChecked = true
            orphanedAt = container.takeOrphanedDatabaseNotice()
        }
    }

    if (orphanedAt != null) {
        DataLostNotice(onDismiss = { orphanedAt = null })
        return
    }

    // Where the app goes is decided by EntryNavigator, not inline here. The two decisions it
    // owns are the two that have already gone wrong: whether a signed-out worker sees the code
    // field or the email form, and whether a freshly activated one is told who they are before
    // the workspace takes over. Both used to be conditions buried in this composable, where
    // the only way to observe them was to install the app and look — which is how the
    // confirmation screen shipped written, wired and unreachable.
    val destination = EntryNavigator.destinationFor(
        session = sessionState,
        hasUnacknowledgedActivation = justActivated != null && !activationAcknowledged,
        showLogin = showLogin
    )

    when (val state = sessionState) {
        SessionState.Initialising -> LoadingScreen()

        SessionState.SignedOut -> {
            // Activation is the front door, not one of two equal options. This is the app a
            // worker uses on a business phone; the owner works in the web portal. Presenting
            // a choice would make every worker stop and decide which kind of person they are,
            // to reach the only answer that was ever going to apply to them.
            //
            // Sign-in is still reachable, and is what an owner or manager with an existing
            // account gets. Nothing about that path changed.
            when (destination) {
                EntryDestination.LOGIN -> {
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

                else -> ActivationScreen(
                    state = activationState,
                    onCodeChanged = activationViewModel::onCodeChanged,
                    onSubmit = {
                        scope.launch {
                            if (activationViewModel.submit()) {
                                // Anything captured before activation still belongs to this
                                // agent and is drained now, exactly as after a sign-in.
                                SyncWorker.enqueue(application)
                            }
                        }
                    },
                    onUseEmailInstead = { showLogin = true }
                )
            }
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
            onStartOver = {
                scope.launch {
                    // Clears the dead session and returns to activation. logout() deliberately
                    // leaves the outbox alone: those transactions are the agent's own record
                    // of work and outlive any authorisation decision made about the handset.
                    container.sessionRepository.logout()
                    showLogin = false
                    activationAcknowledged = false
                }
            }
        )

        is SessionState.Active -> if (destination == EntryDestination.ACTIVATION_CONFIRMED) {
            // Has to outlive the state change that caused it: activation issues a real
            // session, so this branch is already the live one by the time the server replies.
            // A worker who never sees this goes from typing a code straight to a dashboard,
            // never told which business they joined or under whose name they record money.
            ActivatedScreen(
                workerName = justActivated!!.workerName,
                organizationName = justActivated.organizationName,
                branchName = justActivated.branchName,
                onContinue = { activationAcknowledged = true }
            )
        } else {
            // Starts optimistic so the first frame does not flash "Offline" before the
            // platform answers; the observer corrects it immediately.
            val isOnline by container.connectivityObserver.isOnline
                .collectAsState(initial = true)

            LaunchedEffect(screen, isOnline) { dashboardViewModel.refresh(isOnline) }

            val dashboardState by dashboardViewModel.state.collectAsState()
            val context = LocalContext.current

            var smsPermissionGranted by remember { mutableStateOf(context.hasSmsPermission()) }

            // Bumped whenever a permission answer comes back, so the catch-up below runs
            // again once permission is given rather than only on the launch that asked.
            var permissionEpoch by remember { mutableStateOf(0) }

            // Both halves, because either one alone means no message reaches the app: the
            // server decides whether this kind of device may capture at all, Android decides
            // whether this installation was allowed to. While this is true the phone is the
            // record and nothing may be typed in beside it.
            val automaticCapture =
                dashboardState.device?.canAttemptSmsCapture == true && smsPermissionGranted

            when (screen) {
                AuthenticatedScreen.DASHBOARD -> {

                    // Both SMS permissions in one ask. They are one permission group, so an
                    // agent who has already allowed messages to be read is not prompted
                    // again — READ_SMS is granted alongside without another dialog, which is
                    // what lets an update start recovering missed alerts on its own.
                    val permissionLauncher = rememberLauncherForActivityResult(
                        ActivityResultContracts.RequestMultiplePermissions()
                    ) { granted ->
                        // A refusal is a legitimate answer, not a failure. Manual capture
                        // continues to work and nothing queued is affected.
                        smsPermissionGranted =
                            granted[Manifest.permission.RECEIVE_SMS] == true
                    }

            // Reads back alerts that arrived while a fault stopped them being recorded, once
            // for each version installed. The update broadcast does this too; this is the
            // belt to its braces, for the phone that was off during the update, or where the
            // permission was only granted afterwards. Capture fingerprints every message, so
            // running it again records nothing twice.
            LaunchedEffect(permissionEpoch) {
                runCatching {
                    val store = context.getSharedPreferences("zazi-catchup", Context.MODE_PRIVATE)
                    val installed = context.packageManager
                        .getPackageInfo(context.packageName, 0).longVersionCode
                    val done = store.getLong("backfilled-version", 0L)
                    if (done < installed) {
                        if (InboxBackfill.canRead(context)) {
                            container.captureRepository()?.let { capture ->
                                InboxBackfill.run(context.applicationContext, capture)
                                application.scheduleSync()
                                dashboardViewModel.refresh(isOnline)
                            }
                            // The wordings this phone could not read, sent masked so they can
                            // be fixed without anybody holding the handset. Best effort: a
                            // failure here costs nothing, and they stay in the agent's queue
                            // either way.
                            runCatching { container.parsingReportRepository.reportUnreadable() }
                            store.edit().putLong("backfilled-version", installed).apply()
                        } else if (smsPermissionGranted && store.getLong("asked-version", 0L) < installed) {
                            // Reading alerts as they arrive and reading them back afterwards
                            // are separate permissions, and an update does not carry the
                            // second across — measured on a real handset, where RECEIVE_SMS
                            // survived and READ_SMS did not. So an agent who already allowed
                            // capture is asked once, and only once per version, rather than
                            // silently getting no catch-up at all.
                            store.edit().putLong("asked-version", installed).apply()
                            permissionLauncher.launch(arrayOf(Manifest.permission.READ_SMS))
                        }
                    }
                }
            }



                    DashboardScreen(
                        state = dashboardState.copy(isAutomaticCapture = automaticCapture),
                        onCapture = { screen = AuthenticatedScreen.CAPTURE },
                        onFilterChanged = { filter ->
                            scope.launch { dashboardViewModel.onFilterChanged(filter, isOnline) }
                        },
                        onSearchChanged = { query ->
                            scope.launch { dashboardViewModel.onSearchChanged(query, isOnline) }
                        },
                        statementBusy = statementBusy,
                        statementError = statementError,
                        onQuickCapture = { type ->
                            captureViewModel.onTypeChanged(type)
                            screen = AuthenticatedScreen.CAPTURE
                        },
                        onCloseDay = {
                            closeResult = null
                            closeError = null
                            screen = AuthenticatedScreen.CLOSE_DAY
                        },
                        onDownloadStatement = { range, kind ->
                            scope.launch {
                                statementBusy = true
                                statementError = null
                                shareDownload(context, container.statementRepository.download(range, kind)) { statementError = it }
                                statementBusy = false
                            }
                        },
                        onDownloadTradingRecord = {
                            scope.launch {
                                statementBusy = true
                                statementError = null
                                shareDownload(context, container.statementRepository.downloadTradingRecord()) { statementError = it }
                                statementBusy = false
                            }
                        },
                        onRequestFloat = {
                            floatSent = null
                            floatError = null
                            floatOpen = true
                            scope.launch { floatRecent = container.floatRequestRepository.mine() }
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
                        // Decides what the sign-out confirmation is allowed to promise.
                        isActivationOnly = state.user.isActivationOnly,
                        smsPermissionGranted = smsPermissionGranted,
                        // Asked for only when the agent taps, never on launch: a permission
                        // prompt before any explanation is how people learn to decline.
                        onRequestSmsPermission = {
                            permissionLauncher.launch(
                                arrayOf(
                                    Manifest.permission.RECEIVE_SMS,
                                    Manifest.permission.READ_SMS
                                )
                            )
                        },
                        onReviewHeld = { screen = AuthenticatedScreen.HELD }
                    )
                    if (floatOpen) {
                        FloatRequestDialog(
                            busy = floatBusy,
                            error = floatError,
                            sent = floatSent,
                            recent = floatRecent,
                            isOnline = isOnline,
                            onSend = { network, amountMinor ->
                                scope.launch {
                                    floatBusy = true
                                    floatError = null
                                    when (val outcome = container.floatRequestRepository.request(network, amountMinor)) {
                                        is FloatRequestOutcome.Sent -> {
                                            floatSent = outcome.request
                                            floatRecent = container.floatRequestRepository.mine()
                                        }
                                        is FloatRequestOutcome.Failed -> floatError = outcome.reason
                                    }
                                    floatBusy = false
                                }
                            },
                            onDismiss = { floatOpen = false }
                        )
                    }
                }

                AuthenticatedScreen.HELD -> {
                    BackHandler { screen = AuthenticatedScreen.DASHBOARD }
                    val held by container.dashboardRepository.observeHeld()
                        .collectAsStateWithLifecycle(initialValue = emptyList())

                    // Runs once on opening, against whatever rules ship today. The queue was
                    // filled by older ones and holds marketing they would have kept; an agent
                    // who opens this and finds loan offers stops opening it.
                    LaunchedEffect(Unit) {
                        container.dashboardRepository.rescanHeld()
                        dashboardViewModel.refresh(isOnline)
                    }

                    HeldMessagesScreen(
                        items = held.map { it.toUiItem() },
                        onRecord = { item ->
                            // Remembered, not settled. Settling here would take the message out
                            // of the queue the moment the form opened, so an agent who changed
                            // their mind, or was interrupted, would lose the only record that
                            // the money ever arrived. It is settled once the transaction is
                            // actually saved, below.
                            recordingHeldEvidenceId = item.evidenceId
                            captureViewModel.prefillFrom(item)
                            screen = AuthenticatedScreen.CAPTURE
                        },
                        onDismiss = { item ->
                            scope.launch {
                                container.dashboardRepository.settleHeld(item.evidenceId, recorded = false)
                                dashboardViewModel.refresh(isOnline)
                            }
                        },
                        onBack = { screen = AuthenticatedScreen.DASHBOARD }
                    )
                }

                AuthenticatedScreen.CLOSE_DAY -> {
                    BackHandler { screen = AuthenticatedScreen.DASHBOARD }

                    // Worked out when the screen opens rather than held in dashboard state:
                    // it is a question only asked at closing time, and asking it on every
                    // dashboard refresh would walk the day's transactions all day long.
                    var missing by remember { mutableStateOf<List<MissingMessage>>(emptyList()) }
                    LaunchedEffect(Unit) {
                        val dayStart = startOfDayUtcMillis()
                        missing = container.dashboardRepository
                            .missingBetween(dayStart, dayStart + DAY_MILLIS)
                            .map { it.toMissingMessage() }
                    }

                    CloseDayScreen(
                        missing = missing,
                        unsentCount = dashboardState.pendingCount + dashboardState.syncingCount +
                            dashboardState.retryingCount,
                        todayCashMinor = dashboardState.todayCashMinor,
                        todayFloatMinor = dashboardState.todayFloatMinor,
                        isOnline = isOnline,
                        busy = closeBusy,
                        error = closeError,
                        result = closeResult,
                        onSubmit = { cashMinor, floatMinor ->
                            scope.launch {
                                closeBusy = true
                                closeError = null
                                when (val outcome = container.dayCloseRepository.close(cashMinor, floatMinor)) {
                                    is DayCloseOutcome.Closed -> closeResult = outcome.result
                                    is DayCloseOutcome.Failed -> closeError = outcome.reason
                                }
                                closeBusy = false
                            }
                        },
                        onBack = { screen = AuthenticatedScreen.DASHBOARD }
                    )

                }

                AuthenticatedScreen.TRANSACTION -> {
                    val transactionId = selectedTransactionId
                    val context = LocalContext.current

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

                    // Asked once per transaction, before the screen offers anything. A
                    // transaction typed in by hand has no provider message behind it, and one
                    // whose message the retention purge has cleared has nothing left to send.
                    var reporting by remember(transactionId) {
                        mutableStateOf(ParsingReportUiState())
                    }

                    LaunchedEffect(transactionId) {
                        val target = transactionId ?: return@LaunchedEffect
                        reporting = reporting.copy(
                            canReport = container.parsingReportRepository.canReport(target) == null
                        )
                    }

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
                        onBack = { leave() },
                        reporting = reporting,
                        onReport = { verdict, note ->
                            val target = transactionId ?: return@TransactionDetailScreen
                            scope.launch {
                                reporting = reporting.copy(isSending = true, failure = null)
                                val outcome = container.parsingReportRepository.report(
                                    clientTransactionId = target,
                                    verdict = verdict,
                                    note = note
                                )
                                reporting = when (outcome) {
                                    // Already reported counts as sent. From where the agent is
                                    // standing both taps worked, and telling them otherwise is
                                    // how they learn to stop reporting.
                                    is ReportOutcome.Sent ->
                                        reporting.copy(isSending = false, sent = true)

                                    is ReportOutcome.Unavailable ->
                                        reporting.copy(
                                            isSending = false,
                                            canReport = false,
                                            failure = "The message for this transaction is no longer on this phone."
                                        )

                                    // Not queued for retry: the outbox retries indefinitely,
                                    // and a customer's message re-sending itself while the
                                    // network flaps is not what the agent agreed to.
                                    is ReportOutcome.Failed ->
                                        reporting.copy(
                                            isSending = false,
                                            failure = "Could not send just now. Try again when you have signal."
                                        )
                                }
                            }
                        },
                        onReportDismissed = { reporting = reporting.copy(failure = null) },
                        // Sent from the agent's own WhatsApp or SMS: no cost to the business,
                        // and it reaches the customer from a number they already know.
                        onSendReceipt = { receipt ->
                            val send = Intent(Intent.ACTION_SEND).apply {
                                type = "text/plain"
                                putExtra(Intent.EXTRA_TEXT, Receipt.forCustomer(receipt, state.device.branchName))
                            }
                            context.startActivity(Intent.createChooser(send, "Send receipt"))
                        }
                    )
                }

                // Enforced on the route, not only by hiding the buttons. A restored back
                // stack, a deep link or a future caller must not be able to reach a form that
                // would write a second version of a transaction the phone already recorded.
                //
                // Except when the agent is finishing a message the phone received and could
                // not read. That is the opposite of inventing a transaction beside automatic
                // capture — it is rescuing one the system already holds evidence for, and it
                // is the only way that money ever reaches the books. Blocking it here made
                // "Record it" on a held message navigate and bounce straight back, so the
                // button did nothing at all on precisely the phones that need it: the ones
                // with capture switched on.
                AuthenticatedScreen.CAPTURE -> if (automaticCapture && recordingHeldEvidenceId == null) {
                    LaunchedEffect(Unit) { screen = AuthenticatedScreen.DASHBOARD }
                } else {
                    val captureState by captureViewModel.state.collectAsState()

                    // Without this, back from capture leaves the application entirely rather
                    // than returning to the dashboard — an agent reaching for "go back" after
                    // recording a transaction would be dropped onto the home screen.
                    BackHandler {
                        captureViewModel.dismissConfirmation()
                        // Nothing was saved, so the held message stays in the queue.
                        recordingHeldEvidenceId = null
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
                                    // The transaction exists now, so the message that prompted
                                    // it can leave the queue. Only now: until this line the
                                    // agent could still have walked away with nothing saved.
                                    recordingHeldEvidenceId?.let { evidenceId ->
                                        container.dashboardRepository.settleHeld(evidenceId, recorded = true)
                                        recordingHeldEvidenceId = null
                                    }
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

/**
 * Hands a downloaded file to the share sheet, so the agent sends it where they keep things:
 * WhatsApp, email, Files. The grant covers this one file and nothing else in the app's storage.
 */
private fun shareDownload(context: Context, result: StatementDownload, onFailed: (String) -> Unit) {
    when (result) {
        is StatementDownload.Saved -> {
            val uri = FileProvider.getUriForFile(context, "${context.packageName}.statements", result.file)
            val send = Intent(Intent.ACTION_SEND).apply {
                type = result.mimeType
                putExtra(Intent.EXTRA_STREAM, uri)
                addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
            }
            context.startActivity(Intent.createChooser(send, "Send or save"))
        }
        is StatementDownload.Failed -> onFailed(result.reason)
    }
}

/** The server's rows for this agent that this handset does not already hold. */
private suspend fun recordedElsewhere(
    container: AppContainer,
    remote: List<RemoteTransaction>
): List<RemoteTransaction> {
    if (remote.isEmpty()) return emptyList()
    val held = container.dashboardRepository.alreadyHeld(
        clientIds = remote.mapNotNull { it.clientTransactionId },
        serverIds = remote.map { it.id }
    )
    return RemoteActivity.notHeldHere(remote, held)
}

/** A stored row in the screen's own terms. Shared by the day view and the customer search. */
private fun RecentTransactionRow.toActivityItem(): ActivityItem = ActivityItem(
    clientTransactionId = clientTransactionId,
    label = CaptureTransactionType.entries
        .firstOrNull { it.name == transactionType }
        ?.label
    // Not every stored type is offerable on the capture form — a reversal or an adjustment can
    // arrive from elsewhere — so an unknown type is shown readably rather than dropped from the
    // agent's own history.
        ?: transactionType.lowercase().replace('_', ' ').replaceFirstChar { it.uppercase() },
    provider = provider,
    amountMinor = amountMinor,
    cashDeltaMinor = cashDeltaMinor,
    atUtcMillis = transactionAtUtcMillis,
    capturedAutomatically = sourceType == "SMS",
    delivery = ActivityDelivery.fromOutboxState(outboxState),
    customerPhone = customerPhoneNumber
)

/**
 * A held message in the words the agent will read.
 *
 * <p>The stored reason is a developer's sentence about evidence quality. What a person needs
 * to know is simpler: this arrived, we could not tell what it was, it is not in your
 * figures.</p>
 */
private fun HeldMessage.toUiItem(): HeldMessageUiItem = HeldMessageUiItem(
    evidenceId = evidenceId,
    providerLabel = when (provider.uppercase()) {
        "MTN" -> "MTN"
        "TELECEL" -> "Telecel"
        "AIRTELTIGO" -> "AirtelTigo"
        else -> "Mobile money"
    },
    amountMinor = amountMinor,
    customerPhoneNumber = customerPhoneNumber,
    arrivedAtLabel = "Arrived " + DateTimeFormatter.ofPattern("d MMM, HH:mm")
        .format(Instant.ofEpochMilli(observedAtUtcMillis).atZone(ZoneId.systemDefault())),
    reason = reason,
    rawMessage = rawMessage
)

/** A gap in the provider's balances, in the words and the clock the agent reads. */
private fun MissingTransactions.Gap.toMissingMessage(): MissingMessage {
    val clock = DateTimeFormatter.ofPattern("HH:mm")
    fun at(millis: Long) =
        clock.format(Instant.ofEpochMilli(millis).atZone(ZoneId.systemDefault()))

    return MissingMessage(
        amountMinor = amountMinor,
        afterLabel = at(afterUtcMillis),
        beforeLabel = at(beforeUtcMillis)
    )
}
