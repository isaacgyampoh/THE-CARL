# R8 rules for the release build.
#
# Everything here exists because the shrinker cannot see a use that happens through
# reflection. The failure mode is not a build error — it is a release build that installs,
# launches, and then cannot serialise a transaction or reach the server, while every debug
# build and every test stays green.

# ─── kotlinx.serialization ───────────────────────────────────────────────────
# Serializers are generated companions looked up by name at runtime. Without these the sync
# payload fails to serialise and an agent's queued work cannot be submitted at all.
-keepattributes *Annotation*, InnerClasses
-dontnote kotlinx.serialization.**

-keepclassmembers class kotlinx.serialization.json.** {
    *** Companion;
}
-keepclasseswithmembers class kotlinx.serialization.json.** {
    kotlinx.serialization.KSerializer serializer(...);
}

-keep,includedescriptorclasses class app.zazi.**$$serializer { *; }
-keepclassmembers class app.zazi.** {
    *** Companion;
}
-keepclasseswithmembers class app.zazi.** {
    kotlinx.serialization.KSerializer serializer(...);
}

# The wire models themselves. Their property names are the JSON field names, so renaming
# them silently changes the contract with the server.
-keep @kotlinx.serialization.Serializable class app.zazi.** { *; }

# ─── Retrofit / OkHttp ───────────────────────────────────────────────────────
# Retrofit reads generic return types and annotations off the interface by reflection.
-keepattributes Signature, Exceptions, RuntimeVisibleAnnotations, RuntimeVisibleParameterAnnotations
-keep,allowobfuscation interface app.zazi.core.data.network.*Api
-keep,allowobfuscation,allowshrinking class kotlin.coroutines.Continuation

-dontwarn okhttp3.**
-dontwarn okio.**
-dontwarn retrofit2.**

# ─── Room ────────────────────────────────────────────────────────────────────
# Entities are mapped by field name by generated code; the generated implementations are
# resolved by class name.
-keep class * extends androidx.room.RoomDatabase { <init>(); }
-keep @androidx.room.Entity class * { *; }
-dontwarn androidx.room.paging.**

# ─── SQLCipher ───────────────────────────────────────────────────────────────
# Native bindings resolved through JNI, which the shrinker cannot follow. Losing these means
# the encrypted database cannot be opened — the agent's local record becomes unreadable.
-keep class net.zetetic.database.** { *; }
-keep class net.zetetic.database.** { *; }
-dontwarn net.zetetic.**
-dontwarn net.zetetic.database.**

# ─── WorkManager ─────────────────────────────────────────────────────────────
# Workers are instantiated by name.
-keep class * extends androidx.work.ListenableWorker { <init>(...); }

# ─── Diagnostics ─────────────────────────────────────────────────────────────
# Keep line numbers so a crash report from a pilot handset is readable, but hide the original
# source file name.
-keepattributes SourceFile, LineNumberTable
-renamesourcefileattribute SourceFile

# ─── Test-only transitives ───────────────────────────────────────────────────
# Truth drags in errorprone annotations that reference javax.lang.model, which does not exist
# on Android. They matter only when instrumentation is run against the release build; the
# shipped application never loads them.
-dontwarn javax.lang.model.**
-dontwarn com.google.errorprone.**
-dontwarn javax.annotation.**
