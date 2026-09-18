# R8 rules that apply ONLY to the pilot build type. Never applied to release.
#
# The pilot build exists so the instrumentation suite can run against R8-minified code, which
# is the only way a missing keep rule shows up before an agent hits it. Running tests that way
# has one requirement the shipped app does not have, and it belongs here rather than in
# proguard-rules.pro so that nothing test-related can reach a release build.

# AndroidJUnitRunner.onCreate resolves androidx.tracing.Trace. The class never appears in the
# instrumentation APK, because AGP omits from the test APK anything the application under test
# already depends on — tracing arrives transitively through WorkManager — and expects the app
# APK to supply it at runtime. The application itself never calls Trace, so R8 quite correctly
# removes it, and then neither APK has it: the test process dies in handleBindApplication with
# NoClassDefFoundError before a single test runs. Keeping it in the pilot APK only restores
# what the runner expects to find, and leaves the release APK free of it.
-keep class androidx.tracing.Trace { *; }

# The same mechanism, one level up. The instrumentation code is Kotlin and reaches stdlib
# entry points (kotlin.LazyKt, and whatever else the test sources happen to touch) that the
# application itself never calls, so R8 removes them from the app APK — and the test APK does
# not carry its own copy, for the reason above. The runner then fails to resolve them.
#
# This is a deliberate trade-off, and it costs something: the pilot APK retains a superset of
# what release retains, so a stripping bug confined to the Kotlin stdlib would show up in
# release and not here. It is kept anyway because the alternative is not running the suite
# against minified code at all, which is how the SQLCipher native-field bug reached a build in
# the first place. Rules specific to this project's own code are unaffected and still prove
# themselves — nothing under app.zazi.** is kept by this file.
-keep class kotlin.** { *; }
-dontwarn kotlin.**

# Same again for coroutines: the tests call runBlocking, which lives in
# kotlinx.coroutines.BuildersKt. The application uses coroutines heavily but never that
# builder, so R8 drops exactly the entry point the tests need.
-keep class kotlinx.coroutines.** { *; }
-dontwarn kotlinx.coroutines.**

# ── The application API the instrumentation drives ───────────────────────────
#
# R8 shrinks the application without any knowledge that a separately-minified test APK will
# call into it. SessionRepository.login has exactly one caller in the app, so R8 inlined it
# and deleted the method; the test APK's call site then resolved a method that no longer
# exists (NoSuchMethodError on an obfuscated name that mapping.txt still lists, because
# mapping keeps inlined frames for stack traces). Every entry point the tests touch has to
# survive for the same reason.
#
# These are listed one by one rather than as -keep class app.zazi.** { *; }, which would be
# easier and would defeat the purpose: a blanket keep would also preserve every @Serializable
# DTO, and a missing serialization keep rule is exactly the kind of defect this build type
# exists to catch. Everything not named here is still minified as release minifies it.
-keep class app.zazi.ZaziApplication { *; }
-keep class app.zazi.AppContainer { *; }
-keep class app.zazi.core.data.session.SessionRepository { *; }
-keep class app.zazi.core.data.session.LoginResult { *; }
-keep class app.zazi.core.data.session.LoginResult$* { *; }
-keep class app.zazi.core.data.session.SessionState { *; }
-keep class app.zazi.core.data.session.SessionState$* { *; }
-keep class app.zazi.core.data.capture.** { *; }
-keep class app.zazi.core.data.repository.CaptureRepository { *; }
-keep class app.zazi.core.data.repository.OutboxRepository { *; }
-keep class app.zazi.core.data.database.** { *; }
-keep class app.zazi.core.data.sync.SyncWorker { *; }
# The companion is a separate class, and keeping only the outer one is not enough:
# SyncWorker.enqueue lives on it, R8 staticised the kept copy in the app APK while the
# separately-minified test APK still called it virtually, and the runtime rejected the
# mismatch with IncompatibleClassChangeError rather than anything that names the cause.
-keep class app.zazi.core.data.sync.SyncWorker$Companion { *; }
-keep class app.zazi.AppContainer$* { *; }
-keep class app.zazi.core.data.repository.CaptureRepository$* { *; }
-keep class app.zazi.core.data.repository.OutboxRepository$* { *; }
-keep class app.zazi.core.data.session.SessionRepository$* { *; }

# The test that verifies queued work survives a process restart drives WorkManager directly:
# getInstance(Context) to obtain it, then getWorkInfosForUniqueWork to wait for the queue to
# settle. The application enqueues through its own wrapper and calls neither, so R8 removes
# both. Kept whole rather than statics-only, because keeping just the accessor moved the
# failure one method along.
-keep class androidx.work.WorkManager { *; }

# Cross-APK signature agreement. The two APKs are minified separately, so any type appearing
# in a signature the tests call must keep the same name in both. R8 renamed ListenableFuture
# to S1.a in the application, which turned getWorkInfosForUniqueWork into
# (String)LS1/a; — present in the APK, but not the method the test APK was compiled against,
# so the runtime reported it missing rather than mismatched. WorkInfo and its nested State are
# read from the returned future for the same reason.
-keep class com.google.common.util.concurrent.ListenableFuture { *; }
-keep class androidx.work.WorkInfo { *; }
-keep class androidx.work.WorkInfo$* { *; }
