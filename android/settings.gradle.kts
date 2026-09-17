pluginManagement {
    repositories {
        google {
            content {
                includeGroupByRegex("com\\.android.*")
                includeGroupByRegex("com\\.google.*")
                includeGroupByRegex("androidx.*")
            }
        }
        mavenCentral()
        gradlePluginPortal()
    }
}

dependencyResolutionManagement {
    repositoriesMode.set(RepositoriesMode.FAIL_ON_PROJECT_REPOS)
    repositories {
        google()
        mavenCentral()
    }
}

rootProject.name = "zazi"

// Three modules, each with a real boundary rather than one per layer for appearance:
//   :core:domain  pure Kotlin/JVM — parsers, ledger rules, identifiers. No Android APIs, so
//                 the financial engine is testable without an emulator or Robolectric.
//   :core:data    Android — Room, Keystore, network, sync. Depends on domain, never the reverse.
//   :app          Android application — DI wiring and the minimal screens.
include(":core:domain")
include(":core:data")
include(":app")
