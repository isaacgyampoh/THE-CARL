plugins {
    alias(libs.plugins.android.application)
    alias(libs.plugins.kotlin.android)
    alias(libs.plugins.kotlin.compose)
}

android {
    namespace = "app.thecarl"
    compileSdk = 35

    defaultConfig {
        applicationId = "app.thecarl"
        minSdk = 26
        targetSdk = 35
        versionCode = 1
        versionName = "0.1.0"
        testInstrumentationRunner = "androidx.test.runner.AndroidJUnitRunner"

        // Base URL is configuration, not a constant. 10.0.2.2 is the host loopback as seen
        // from an emulator; release builds override it. No credential or secret is ever a
        // build-config value.
        // 10.0.2.2 is the emulator's alias for the host loopback. Port 5055 avoids
        // macOS AirPlay Receiver, which occupies 5000 and answers 403.
        buildConfigField("String", "API_BASE_URL", "\"http://10.0.2.2:5055/\"")
    }

    buildTypes {
        debug {
            isMinifyEnabled = false
        }
        release {
            isMinifyEnabled = true
            isShrinkResources = true
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"), "proguard-rules.pro")
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
