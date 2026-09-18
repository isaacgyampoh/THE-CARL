# R8 rules for the instrumentation APK when tests run against the release build.
#
# The test APK is shrunk separately from the application, so it does not inherit
# proguard-rules.pro. Nothing here reaches the shipped application.

# The runner finds tests by reading @RunWith and @Test off the class. Annotations are
# discarded by default, so without this the suite reports "0 tests" and passes — the worst
# possible outcome, because it looks like success.
-keepattributes *Annotation*, Signature, InnerClasses, EnclosingMethod

# The test APK is never shipped, so there is nothing to gain by shrinking its own classes.
-keep class app.zazi.** { *; }
-keep class androidx.test.** { *; }
-dontwarn androidx.test.**

# Truth drags in errorprone annotations that reference javax.lang.model, which does not exist
# on Android and is never loaded at runtime.
-dontwarn javax.lang.model.**
-dontwarn com.google.errorprone.**
-dontwarn javax.annotation.**
-dontwarn com.google.common.**

# JUnit and the AndroidX runner discover test classes and methods by reflection. Renaming or
# removing them leaves a run that reports no tests rather than failing.
-keep class * extends junit.framework.TestCase { *; }
-keep class org.junit.** { *; }
-keep @org.junit.runner.RunWith class * { *; }
-keepclassmembers class * {
    @org.junit.Test *;
    @org.junit.Before *;
    @org.junit.After *;
}
-keep class app.zazi.**Test { *; }
-dontwarn org.junit.**

# Note: androidx.tracing is deliberately NOT kept here. AGP leaves out of the test APK
# anything the application under test already depends on, so a keep rule in this file has
# nothing to act on — the classes are not an input to the test APK's R8 run at all. The rule
# that matters lives in proguard-rules-pilot.pro, on the application side.
