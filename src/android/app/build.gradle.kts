plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.plugin.compose")
}

android {
    namespace = "com.bob.android"
    compileSdk = 36

    defaultConfig {
        applicationId = "com.bob.android"
        minSdk = 34
        targetSdk = 36
        versionCode = 1
        versionName = "0.1.0"
        testInstrumentationRunner = "com.bob.android.device.BobDeviceChecks"
    }

    buildFeatures {
        compose = true
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    buildTypes {
        release {
            isMinifyEnabled = false
            proguardFiles(
                getDefaultProguardFile("proguard-android-optimize.txt"),
                "proguard-rules.pro",
            )
        }
    }

    packaging {
        resources.excludes += "/META-INF/{AL2.0,LGPL2.1}"
    }
}

dependencies {
    val composeBom = platform("androidx.compose:compose-bom:2026.06.00")

    implementation(composeBom)
    implementation("androidx.activity:activity-compose:1.13.0")
    implementation("androidx.compose.foundation:foundation")
    implementation("androidx.compose.material3:material3")
    implementation("androidx.compose.ui:ui")
    implementation("androidx.lifecycle:lifecycle-runtime-compose:2.10.0")
    implementation("androidx.lifecycle:lifecycle-viewmodel-compose:2.10.0")
    implementation("com.squareup.okhttp3:okhttp:5.4.0")
    implementation("org.jetbrains.kotlinx:kotlinx-coroutines-android:1.10.2")
}

val streamingChecks by tasks.registering(JavaExec::class) {
    group = "verification"
    description = "Runs bounded-memory streaming and filename checks without a test dependency."
    dependsOn("compileDebugKotlin", "compileDebugUnitTestKotlin")
    mainClass.set("com.bob.android.transfer.TransferStreamChecksKt")
    maxHeapSize = "128m"
    classpath = files(
        provider { tasks.getByName("compileDebugKotlin").outputs.files },
        provider { tasks.getByName("compileDebugUnitTestKotlin").outputs.files },
    ) + configurations.getByName("debugUnitTestRuntimeClasspath")
}

tasks.named("check") {
    dependsOn(streamingChecks)
}

tasks.withType<Test>().configureEach {
    // The plain main harness runs through streamingChecks. Future framework tests
    // keep Gradle's normal discovery and failure behavior.
    exclude("**/TransferStreamChecksKt*.class", "**/ZeroStream.class")
    val frameworkTestSources = fileTree("src") {
        include("test*/**/*.kt", "test*/**/*.java")
        exclude("test/java/com/bob/android/transfer/TransferStreamChecks.kt")
    }
    // Gradle finalizes this setting before task actions run. Decide from the
    // source set during configuration; adding any other test restores strictness.
    failOnNoDiscoveredTests = !frameworkTestSources.isEmpty
}
