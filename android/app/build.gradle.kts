plugins {
    alias(libs.plugins.android.application)
    alias(libs.plugins.kotlin.android)
    alias(libs.plugins.kotlin.compose)
}

android {
    namespace = "app.zazi"
    compileSdk = 36

    defaultConfig {
        applicationId = "app.zazi"
        minSdk = 26
        // Android 16. Google Play refuses new apps and updates below this since 31 August 2026,
        // so this is what "publishable" means rather than a preference.
        targetSdk = 36
        // The release this build actually is. It had stayed at the first-commit values while
        // the product was verified and reported as v2, which is the kind of drift that ends
        // with two different builds claiming the same version in a support conversation.
        // 12, because 11 is already on the phones this update has to reach. Android
        // refuses an install whose version is not higher, so shipping the fix under the
        // same number would have left every existing agent on the broken build with no
        // sign anything was wrong.
        versionCode = 13
        versionName = "2.8.2"
        testInstrumentationRunner = "androidx.test.runner.AndroidJUnitRunner"

        // Base URL is configuration, not a constant, and a pilot on a real handset needs a
        // different one from an emulator: 10.0.2.2 is the emulator's alias for the host
        // loopback and means nothing on a phone. Override without editing this file:
        //
        //   ./gradlew assembleDebug -PapiBaseUrl=http://192.168.1.20:5055/
        //
        // Port 5055 avoids macOS AirPlay Receiver, which occupies 5000 and answers 403.
        // No credential or secret is ever a build-config value.
        // The emulator default is a development convenience and must never leave with a
        // release. It did: a release APK built without -PapiBaseUrl carried
        // http://10.0.2.2:5055/, which is the emulator's alias for the host loopback and
        // resolves to nothing on a handset — and is cleartext besides, which the release
        // manifest forbids. The build looked fine and the app could not reach a server.
        //
        // assembleRelease now refuses to run without an explicit URL. See the guard below.
        val apiBaseUrl = (project.findProperty("apiBaseUrl") as String?)
            ?: "http://10.0.2.2:5055/"

        buildConfigField("String", "API_BASE_URL", "\"$apiBaseUrl\"")
    }

    // Signing material never lives in the repository. A release build is signed when the
    // four properties are supplied — by CI, or by a local gradle.properties that is not
    // committed — and is left unsigned otherwise rather than failing the build, so an
    // unsigned release can still be produced for inspection.
    // Signing properties belong in ~/.gradle/gradle.properties, or on the command line with
    // -P. The project's own gradle.properties is tracked by git, so a password written there is
    // one `git commit -a` away from being published — and a keystore password in history cannot
    // be retracted, only rotated. Fail loudly rather than sign quietly.
    val trackedProperties = rootProject.file("gradle.properties")
    if (trackedProperties.exists()) {
        val lines = trackedProperties.readLines()
        val leaked = listOf("zaziKeystore", "zaziKeystorePassword", "zaziKeyAlias", "zaziKeyPassword")
            .filter { name -> lines.any { it.trimStart().startsWith("$name=") } }
        if (leaked.isNotEmpty()) {
            throw GradleException(
                "Signing properties (${leaked.joinToString()}) are set in android/gradle.properties, " +
                    "which is tracked by git. Move them to ~/.gradle/gradle.properties, or pass " +
                    "them with -P on the command line."
            )
        }
    }

    val releaseStore = (project.findProperty("zaziKeystore") as String?)?.let(::file)

    signingConfigs {
        if (releaseStore != null && releaseStore.exists()) {
            create("release") {
                storeFile = releaseStore
                storePassword = project.findProperty("zaziKeystorePassword") as String?
                keyAlias = project.findProperty("zaziKeyAlias") as String?
                keyPassword = project.findProperty("zaziKeyPassword") as String?

                // v1 is off because nothing below API 24 can install this app anyway. v3 is
                // on so the upload key can be rotated later without abandoning the app; the
                // default leaves only v2, which cannot carry proof of rotation.
                enableV1Signing = false
                enableV2Signing = true
                enableV3Signing = true
            }
        }
    }

    // Instrumentation normally runs against the debug build. Pointing it at release runs the
    // same suite against R8-minified code, which is the only way to catch a keep rule that is
    // missing: the failure never appears in a debug build, a unit test, or a successful
    // release build — only at runtime, when a serializer or a Retrofit interface has been
    // stripped and an agent's queued work cannot be submitted.
    //
    //   ./gradlew :app:connectedAndroidTest -PtestBuildType=release -PzaziKeystore=…
    testBuildType = (project.findProperty("testBuildType") as String?) ?: "debug"

    buildTypes {
        debug {
            isMinifyEnabled = false
        }
        release {
            signingConfig = signingConfigs.findByName("release")
            isMinifyEnabled = true
            isShrinkResources = true
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"), "proguard-rules.pro")
            // The instrumentation APK is shrunk separately and does not inherit the rules
            // above, so running tests against release needs its own.
            testProguardFiles("test-proguard-rules.pro")
        }

        // Minified and signed exactly like release, but permitted to reach a backend on the
        // local network over plain HTTP. It exists so a trial can run on someone's office
        // wifi, and so the instrumentation suite can be run against R8-minified code — a
        // missing keep rule shows up nowhere else. Never ship it.
        create("pilot") {
            // Declared after release, and that ordering is load-bearing: initWith copies a
            // build type as it stands at that moment. Declared first, it copied release
            // before proguardFiles had been added, producing a build that was minified with
            // no keep rules at all — which stripped the field SQLCipher's native library
            // resolves by name, and the app aborted on startup.
            initWith(getByName("release"))
            matchingFallbacks += "release"

            // Debug signing, deliberately — initWith copied release's signing config along
            // with everything else, which meant the production key signed pilot builds too.
            // A trial handset on somebody's office wifi has no business carrying the identity
            // that controls every future update of app.zazi, and the production key should
            // leave its keystore for exactly one build type.
            //
            // Debug rather than unsigned because an unsigned APK will not install at all.
            signingConfig = signingConfigs.getByName("debug")

            isMinifyEnabled = true
            isShrinkResources = true

            // Appended to the rules inherited from release, not a replacement for them. Holds
            // the one thing running instrumentation against minified code needs, so release
            // never carries it.
            proguardFile("proguard-rules-pilot.pro")

            // Cleartext is permitted by src/pilot's network security config. It is scoped
            // to this build type rather than to a host list, because a pilot's server
            // address is whichever laptop is running it and pinning that would mean either
            // generating the file per build or committing someone's IP.
        }

    }

    buildFeatures {
        // Compose is deliberately off until there is Compose source to compile. This module
        // currently contains the application graph and worker wiring only; enabling the
        // feature and its dependencies for code that does not exist is dead configuration,
        // and it made AGP's Compose lint detectors crash analysing a module with no
        // composables. It returns with the first screen.
        buildConfig = true
        compose = true
    }

    lint {
        // Path-scoped suppressions live in lint.xml so they carry their reason with them.
        lintConfig = file("lint.xml")

        // AGP 8.7's NonNullableMutableLiveDataDetector throws NoClassDefFoundError when
        // lifecycle-livedata is not on the classpath, crashing the entire lint run. This
        // module uses no LiveData, so the detector has nothing to inspect. Disabling a
        // broken tool is preferable to adding an unused dependency to appease it — this
        // suppresses a tooling defect, not a finding.
        disable += "NullSafeMutableLiveData"
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    kotlinOptions {
        jvmTarget = "17"
    }

    testOptions {
        unitTests {
            isIncludeAndroidResources = true
            isReturnDefaultValues = true
        }
    }
}

dependencies {
    implementation(project(":core:data"))

    implementation(libs.androidx.work.runtime.ktx)
    implementation(libs.kotlinx.coroutines.android)
    implementation(libs.retrofit)
    implementation(libs.retrofit.kotlinx.serialization)
    implementation(libs.okhttp)
    implementation(libs.kotlinx.serialization.json)

    implementation(libs.androidx.core.ktx)
    implementation(libs.androidx.lifecycle.runtime.ktx)
    implementation(libs.androidx.lifecycle.viewmodel.compose)
    implementation(libs.androidx.lifecycle.runtime.compose)
    implementation(libs.androidx.activity.compose)
    implementation(platform(libs.androidx.compose.bom))
    implementation(libs.androidx.compose.ui)
    implementation(libs.androidx.compose.material3)
    implementation(libs.androidx.compose.ui.tooling.preview)
    debugImplementation(libs.androidx.compose.ui.tooling)

    testImplementation(libs.junit)
    testImplementation(libs.truth)
    testImplementation(libs.kotlinx.coroutines.test)

    // Instrumentation: the bulk sync test drives the real AppContainer graph on a device,
    // so it lives here rather than in :core:data where the application does not exist.
    androidTestImplementation(libs.androidx.test.junit)
    androidTestImplementation(libs.androidx.test.core)
    androidTestImplementation(libs.androidx.test.runner)
    androidTestImplementation(libs.truth)
    androidTestImplementation(libs.kotlinx.coroutines.test)
    // Test-only. :core:data keeps Room as an implementation detail, which is right — the
    // application does not touch DAOs. The bulk sync test asserts the final state of
    // specific outbox rows, so it needs the type on its own classpath. Adding a lookup to
    // OutboxRepository purely to serve a test would put test shape into production API.
    androidTestImplementation(libs.androidx.room.runtime)
}

// A release must name the server it talks to.
//
// Without this, assembleRelease silently inherited the emulator's loopback address — a URL
// that cannot resolve on a phone, over a scheme the release manifest refuses. The failure
// surfaced only as an app that could not reach anything, long after the build was called
// green.
//
// The property is read here, at configuration time, and only the resulting value crosses
// into the task. Reading `project` inside doFirst is unsupported with the configuration
// cache, and doing so made this guard reject a URL that had in fact been supplied.
//
// Required to be HTTPS because release sets usesCleartextTraffic=false: an http:// URL here
// produces an APK blocked by its own manifest.
// Addresses that are syntactically fine and cannot serve a real handset.
//
// Checked because the scheme check alone let https://localhost/ and https://api.example.com/
// through, which fail in exactly the way the emulator default did: silently, at run time, on
// somebody else's phone.
val nonProductionHosts = listOf(
    "localhost",
    "127.0.0.1",
    "0.0.0.0",
    "[::1]",
    // The emulator's alias for the host loopback.
    "10.0.2.2",
    // Reserved by RFC 2606 and RFC 6761 precisely so they cannot be real.
    "example.com", "example.net", "example.org",
    ".invalid", ".test", ".localhost",
    // Multicast DNS. Resolves on a LAN and nowhere else.
    ".local"
)

// Resolved to a plain message at configuration time. Only a String crosses into the task:
// referencing a script-level value from inside doFirst captures the build script itself,
// which the configuration cache cannot serialize.
val releaseUrlProblem: String? = (project.findProperty("apiBaseUrl") as String?).let { supplied ->
    when {
        supplied == null ->
            "A release build needs the production API base URL. Supply it explicitly:\n" +
                "  ./gradlew assembleRelease -PapiBaseUrl=https://api.example.com/\n" +
                "Without it the build would inherit the emulator address " +
                "http://10.0.2.2:5055/, which resolves to nothing on a handset and is cleartext."

        !supplied.startsWith("https://") ->
            "The release API base URL must be https. Got: $supplied\n" +
                "Release builds set usesCleartextTraffic=false, so a cleartext URL produces " +
                "an APK blocked by its own manifest."

        // https alone is not enough. https://localhost/ and https://api.example.com/ are
        // well-formed, pass a scheme check, and produce an app that reaches nothing — the
        // same class of failure as the emulator address, arriving by a different route.
        nonProductionHosts.any { supplied.contains(it, ignoreCase = true) } ->
            "The release API base URL is not a production address. Got: $supplied\n" +
                "Loopback, emulator and reserved-example hosts cannot be reached from a " +
                "handset. Supply the real API hostname, e.g. https://api.yourdomain/."

        else -> null
    }
}

// A release must be signed, and must say so when it cannot be.
//
// The signing config is created only when all four properties resolve and the keystore file
// exists. Until now the absence of any of them produced an unsigned APK and a green build —
// which is how an unsigned release survived a full verification pass: nothing failed, and
// the artifact looked like every other artifact.
//
// Resolved at configuration time and reduced to a message, for the same configuration-cache
// reason as the URL guard above: reading `project` inside doFirst is unsupported.
//
// Only `release` is checked. Debug and pilot must keep building on a machine that has never
// seen the production keystore, which is most of them.
val releaseSigningProblem: String? = run {
    val required = listOf(
        "zaziKeystore" to "path to the production keystore, outside the repository",
        "zaziKeystorePassword" to "keystore password",
        "zaziKeyAlias" to "key alias inside the keystore",
        "zaziKeyPassword" to "key password"
    )

    val missing = required
        .filter { (name, _) -> (project.findProperty(name) as String?).isNullOrBlank() }

    val keystorePath = project.findProperty("zaziKeystore") as String?

    when {
        missing.isNotEmpty() ->
            "A release build must be signed, and these signing properties are missing:\n" +
                missing.joinToString("\n") { (name, purpose) -> "  $name  — $purpose" } +
                "\n\nPut them in ~/.gradle/gradle.properties, which is outside this " +
                "repository and never committed. Do not put them in " +
                "android/gradle.properties: that file is tracked, and a keystore password " +
                "in git history cannot be retracted, only rotated."

        // A path that resolves to nothing is the same failure wearing a different hat: the
        // signing config is silently not created and the APK comes out unsigned.
        !file(keystorePath!!).exists() ->
            "zaziKeystore points at a file that does not exist:\n  $keystorePath\n\n" +
                "Without it no signing config is created and the release would be written " +
                "unsigned."

        else -> null
    }
}

// Guarded at the point the URL is baked in, not at the end.
//
// Attaching this to assembleRelease was not enough: that task runs after its dependencies,
// so a rejected build still left a complete APK in the output directory carrying the very
// URL that had just been refused. A failing build that leaves a shippable artifact behind is
// worse than no guard, because the failure is easy to miss and the file is easy to pick up.
//
// packageRelease is the task that writes the APK, and bundleRelease the AAB, so failing them
// means no artifact is ever created. assembleRelease stays as a backstop.
//
// Deliberately NOT preReleaseBuild or generateReleaseBuildConfig, which looked like the
// earliest possible point and broke `./gradlew test`: the release-variant unit tests depend
// on them, and a guard about shipping has no business stopping anyone running tests.
tasks.matching {
    it.name in setOf(
        "packageRelease",
        "bundleRelease",
        "assembleRelease"
    )
}.configureEach {
    val urlProblem = releaseUrlProblem
    val signingProblem = releaseSigningProblem
    doFirst {
        if (urlProblem != null) {
            throw GradleException(urlProblem)
        }
        if (signingProblem != null) {
            throw GradleException(signingProblem)
        }
    }
}
