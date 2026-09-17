plugins {
    alias(libs.plugins.kotlin.jvm)
    alias(libs.plugins.kotlin.serialization)
}

// Pure Kotlin/JVM on purpose. The financial engine — parsers, ledger rules, identifiers —
// has no Android dependency, so its tests run in milliseconds on the JVM without an
// emulator or Robolectric. An Android dependency creeping in here would be a design defect.
kotlin {
    jvmToolchain(21)
}

dependencies {
    implementation(libs.kotlinx.coroutines.core)
    implementation(libs.kotlinx.serialization.json)

    testImplementation(libs.junit)
    testImplementation(libs.truth)
    testImplementation(libs.kotlinx.coroutines.test)
    testImplementation(libs.kotlinx.serialization.json)
}

tasks.withType<Test>().configureEach {
    // The cross-platform SMS contract corpus lives at the repository root and is read by
    // both this suite and the .NET one. Passed as a property rather than copied: a copy
    // could drift from the original, which is exactly what the corpus exists to prevent.
    systemProperty("zazi.contracts.dir", rootProject.projectDir.parentFile.resolve("contracts").absolutePath)
}
