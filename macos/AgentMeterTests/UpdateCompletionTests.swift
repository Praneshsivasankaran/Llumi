import Foundation
import XCTest

@MainActor final class UpdateCompletionTests: XCTestCase {
  private var defaults: UserDefaults!
  private let hostPath = "/tmp/Llumi-UpdateCompletion-Fixture.app"
  private var pendingKey: String { UpdateCompletionTracker.persistenceKeys(forHostPath: hostPath)!.pending }
  private var acknowledgedKey: String { UpdateCompletionTracker.persistenceKeys(forHostPath: hostPath)!.acknowledged }
  private func withDefaults(_ body: () -> Void) {
    let suite = "Llumi-UpdateCompletion-Test-" + UUID().uuidString
    defaults = UserDefaults(suiteName: suite)
    defer {
      defaults.removePersistentDomain(forName: suite)
      defaults = nil
    }
    body()
  }
  private func tracker(_ version: String? = "1.1.2", _ build: String? = "2", hostPath: String? = nil) -> UpdateCompletionTracker {
    UpdateCompletionTracker(defaults: defaults, currentVersion: version, currentBuild: build, hostPath: hostPath ?? self.hostPath)
  }
  private func prepare() -> UpdateCompletionTracker {
    let value = tracker()
    XCTAssertTrue(value.markInstallationIntent(targetVersion: "1.1.3", targetBuild: "3"))
    return value
  }
  private func installAndAcknowledge() {
    _ = prepare()
    let installed = tracker("1.1.3", "3")
    XCTAssertNotNil(installed.completion)
    installed.acknowledgeCompletion()
  }

  func testFirstInstallationOrdinaryLaunchAndUnrelatedSetupPreferencesDoNotClaimSuccess() {
    withDefaults {
      defaults.set(true, forKey: "setupComplete")
      defaults.set("1.1.2", forKey: "previousAppVersion")
      for _ in 0..<3 { XCTAssertNil(tracker("1.1.3", "3").completion) }
      XCTAssertNil(defaults.object(forKey: pendingKey))
      XCTAssertNil(defaults.object(forKey: acknowledgedKey))
    }
  }
  func testLegacyFirstHopWithoutThisHostsInstallIntentStaysQuiet() {
    withDefaults {
      defaults.set(true, forKey: "llumiSetupCompleted")
      defaults.set("1.1.2", forKey: "lastSeenVersion")
      defaults.set(true, forKey: "SUAutomaticallyUpdate")
      XCTAssertNil(tracker("1.1.3", "3").completion)
      XCTAssertNil(defaults.object(forKey: acknowledgedKey))
    }
  }
  func testChecksOrDownloadsWithoutAnInstallationIntentNeverClaimSuccess() {
    withDefaults {
      defaults.set("1.1.3", forKey: "availableVersion")
      defaults.set(true, forKey: "downloadComplete")
      XCTAssertNil(tracker("1.1.3", "3").completion)
    }
  }
  func testPreparedUpdateAndLaterSurviveOldVersionRelaunchWithoutSuccess() {
    withDefaults {
      let original = prepare()
      XCTAssertNil(original.completion)
      for _ in 0..<3 {
        XCTAssertNil(tracker().completion)
        XCTAssertNotNil(defaults.object(forKey: pendingKey))
      }
      XCTAssertNotNil(tracker("1.1.3", "3").completion)
    }
  }
  func testExactInstalledTargetCreatesTrustedVersionSpecificNotice() {
    withDefaults {
      _ = prepare()
      let installed = tracker("1.1.3", "3")
      XCTAssertEqual(installed.completion?.version, "1.1.3")
      XCTAssertEqual(installed.completion?.build, "3")
      XCTAssertEqual(installed.completion?.releaseNotesURL.absoluteString,
        "https://tryllumi.com/releases/macos/1.1.3/")
    }
  }
  func testCompletionPersistsUntilExplicitAcknowledgmentThenNeverRepeats() {
    withDefaults {
      _ = prepare()
      for _ in 0..<3 {
        XCTAssertNotNil(tracker("1.1.3", "3").completion)
        XCTAssertNil(defaults.object(forKey: acknowledgedKey))
      }
      let installed = tracker("1.1.3", "3")
      installed.acknowledgeCompletion()
      XCTAssertNil(installed.completion)
      XCTAssertNil(defaults.object(forKey: pendingKey))
      for _ in 0..<3 { XCTAssertNil(tracker("1.1.3", "3").completion) }
    }
  }
  func testCheckingAgainCannotEraseAnUnacknowledgedInstalledCompletion() {
    withDefaults {
      _ = prepare()
      let installed = tracker("1.1.3", "3")
      installed.failedInstallation()
      XCTAssertNotNil(installed.completion)
      XCTAssertNotNil(tracker("1.1.3", "3").completion)
      XCTAssertFalse(installed.markInstallationIntent(targetVersion: "1.1.4", targetBuild: "4"))
    }
  }
  func testCanceledAndFailedInstallationsNeverShowCompletion() {
    withDefaults {
      for specific in [false, true] {
        let original = prepare()
        if specific { original.failedInstallation(targetVersion: "1.1.3", targetBuild: "3") }
        else { original.failedInstallation() }
        XCTAssertNil(defaults.object(forKey: pendingKey))
        XCTAssertNil(tracker("1.1.3", "3").completion)
      }
    }
  }
  func testLateFailureFromAnotherTargetDoesNotEraseThePreparedUpdate() {
    withDefaults {
      let original = prepare()
      original.failedInstallation(targetVersion: "1.1.4", targetBuild: "4")
      original.failedInstallation(targetVersion: "1.1.3", targetBuild: nil)
      XCTAssertNotNil(defaults.object(forKey: pendingKey))
      XCTAssertNotNil(tracker("1.1.3", "3").completion)
    }
  }
  func testDuplicatePreparedAndActualInstallCallbacksKeepTheSameIntent() {
    withDefaults {
      let original = prepare()
      XCTAssertTrue(original.markInstallationIntent(targetVersion: "1.1.3", targetBuild: "3"))
      XCTAssertNotNil(tracker("1.1.3", "3").completion)
    }
  }
  func testMismatchedVersionOrBuildAndDowngradeDoNotShowCompletion() {
    withDefaults {
      for identity in [("1.1.3", "4"), ("1.1.4", "3"), ("1.1.1", "1"), ("1.1.2", "3")] {
        _ = prepare()
        XCTAssertNil(tracker(identity.0, identity.1).completion)
        XCTAssertNil(defaults.object(forKey: pendingKey))
      }
    }
  }
  func testDowngradeAndUnchangedOrLowerBuildCannotCreateIntent() {
    withDefaults {
      let current = tracker("1.1.3", "3")
      for target in [("1.1.2", "4"), ("1.1.3", "3"), ("1.1.4", "2"), ("1.1.4", "3")] {
        XCTAssertFalse(current.markInstallationIntent(targetVersion: target.0, targetBuild: target.1))
      }
      XCTAssertNil(defaults.object(forKey: pendingKey))
    }
  }
  func testSameMarketingVersionWithHigherBuildIsTrackedExactlyOnce() {
    withDefaults {
      let current = tracker("1.1.3", "3")
      XCTAssertTrue(current.markInstallationIntent(targetVersion: "1.1.3", targetBuild: "4"))
      let next = tracker("1.1.3", "4")
      XCTAssertNotNil(next.completion)
      next.acknowledgeCompletion()
      XCTAssertNil(tracker("1.1.3", "4").completion)
    }
  }
  func testAcknowledgmentHighWaterPreventsReannouncingAnOlderRelease() {
    withDefaults {
      installAndAcknowledge()
      XCTAssertFalse(tracker().markInstallationIntent(targetVersion: "1.1.3", targetBuild: "3"))
      let current = tracker("1.1.3", "3")
      XCTAssertTrue(current.markInstallationIntent(targetVersion: "1.1.4", targetBuild: "4"))
      let next = tracker("1.1.4", "4")
      next.acknowledgeCompletion()
      XCTAssertFalse(tracker().markInstallationIntent(targetVersion: "1.1.3", targetBuild: "3"))
    }
  }
  func testTamperedReplayOfAcknowledgedIntentDoesNotRepeatCompletion() {
    withDefaults {
      _ = prepare()
      let oldIntent = defaults.object(forKey: pendingKey)
      tracker("1.1.3", "3").acknowledgeCompletion()
      defaults.set(oldIntent, forKey: pendingKey)
      XCTAssertNil(tracker("1.1.3", "3").completion)
      XCTAssertNil(defaults.object(forKey: pendingKey))
    }
  }
  func testMalformedPendingMetadataCannotClaimSuccess() {
    withDefaults {
      let origin = ["version": "1.1.2", "build": "2"]
      let target = ["version": "1.1.3", "build": "3"]
      let values: [Any] = [true, "1.1.3", 3, [], [:],
        ["schema": "2", "origin": origin, "target": target],
        ["schema": true, "origin": origin, "target": target],
        ["schema": "1", "origin": origin],
        ["schema": "1", "origin": target, "target": origin],
        ["schema": "1", "origin": origin, "target": target, "url": "https://evil.invalid/"]]
      for value in values {
        defaults.set(value, forKey: pendingKey)
        XCTAssertNil(tracker("1.1.3", "3").completion)
        XCTAssertNil(defaults.object(forKey: pendingKey))
      }
    }
  }
  func testMalformedAcknowledgmentFailsClosedButFreshInstallIntentCanRecover() {
    withDefaults {
      _ = prepare()
      defaults.set(["schema": "1", "release": ["version": "1.1.3", "build": false]],
        forKey: acknowledgedKey)
      XCTAssertNil(tracker("1.1.3", "3").completion)
      XCTAssertNil(defaults.object(forKey: pendingKey))
      XCTAssertTrue(tracker().markInstallationIntent(targetVersion: "1.1.3", targetBuild: "3"))
      XCTAssertNotNil(tracker("1.1.3", "3").completion)
    }
  }
  func testInvalidTrustedBundleIdentityFailsClosed() {
    withDefaults {
      _ = prepare()
      for identity: (String?, String?) in [(nil, "3"), ("1.1.3", nil), ("garbage", "3"), ("1.1.3", "-1")] {
        let invalid = tracker(identity.0, identity.1)
        XCTAssertNil(invalid.completion)
        XCTAssertFalse(invalid.markInstallationIntent(targetVersion: "1.1.4", targetBuild: "4"))
      }
    }
  }
  func testVersionAndBuildValidationRejectsPathsSchemesWhitespaceAndOverflow() {
    withDefaults {
      let versions = ["https://evil.invalid/", "1.1.3/../../evil", "1.1.3?x=y", "1.1.3#fragment", "1.1", "1.1.3-beta", "01.1.3", "1.01.3", " 1.1.3", "1.1.3\n", "１.１.３", String(repeating: "9", count: 30) + ".1.3"]
      for version in versions {
        XCTAssertNil(UpdateReleaseIdentity(version: version, build: "3"))
        XCTAssertNil(UpdateCompletionNotice.releaseNotesURL(version: version))
      }
      for build in ["", "0", "03", "-1", "3.0", " 3", "3\n", "٣", String(repeating: "9", count: 19)] {
        XCTAssertNil(UpdateReleaseIdentity(version: "1.1.3", build: build))
      }
      XCTAssertNotNil(UpdateReleaseIdentity(version: "1.1.3", build: "3"))
    }
  }
  func testAcknowledgingWithoutCompletionDoesNotWriteACompletionRecord() {
    withDefaults {
      tracker().acknowledgeCompletion()
      XCTAssertNil(defaults.object(forKey: acknowledgedKey))
      _ = prepare()
      tracker().acknowledgeCompletion()
      XCTAssertNotNil(defaults.object(forKey: pendingKey))
      XCTAssertNil(defaults.object(forKey: acknowledgedKey))
    }
  }
  func testCompletionStateContainsOnlyVersionAndBuildMetadata() {
    withDefaults {
      _ = prepare()
      let pending = defaults.dictionary(forKey: pendingKey)
      XCTAssertEqual(Set(pending?.keys.map { $0 } ?? []), Set(["schema", "origin", "target"]))
      let installed = tracker("1.1.3", "3")
      installed.acknowledgeCompletion()
      let acknowledged = defaults.dictionary(forKey: acknowledgedKey)
      XCTAssertEqual(Set(acknowledged?.keys.map { $0 } ?? []), Set(["schema", "release"]))
      XCTAssertNil(defaults.object(forKey: "releaseNotesURL"))
    }
  }
  func testPreviewCannotConsumeAcknowledgeFailOrClearAnotherHostsIntent() {
    withDefaults {
      _ = prepare()
      let otherPath = "/tmp/Llumi-OtherPreview-Fixture.app"
      let pending = defaults.dictionary(forKey: pendingKey)! as NSDictionary
      let other = tracker("1.1.3", "3", hostPath: otherPath)
      XCTAssertNil(other.completion)
      other.acknowledgeCompletion()
      other.failedInstallation()
      XCTAssertEqual(defaults.dictionary(forKey: pendingKey)! as NSDictionary, pending)
      XCTAssertNotNil(tracker("1.1.3", "3").completion)
      XCTAssertNil(defaults.object(forKey: acknowledgedKey))
    }
  }
  func testDifferentHostsHaveIndependentIntentAndAcknowledgmentHighWater() {
    withDefaults {
      installAndAcknowledge()
      let otherPath = "/tmp/Llumi-OtherPreview-Fixture.app"
      let otherOrigin = tracker(hostPath: otherPath)
      XCTAssertTrue(otherOrigin.markInstallationIntent(targetVersion: "1.1.3", targetBuild: "3"))
      let otherInstalled = tracker("1.1.3", "3", hostPath: otherPath)
      XCTAssertNotNil(otherInstalled.completion)
      otherInstalled.acknowledgeCompletion()
      XCTAssertNil(tracker("1.1.3", "3").completion)
      let otherKeys = UpdateCompletionTracker.persistenceKeys(forHostPath: otherPath)!
      XCTAssertNotEqual(otherKeys.acknowledged, acknowledgedKey)
      XCTAssertNotNil(defaults.object(forKey: otherKeys.acknowledged))
      XCTAssertNotNil(defaults.object(forKey: acknowledgedKey))
    }
  }
  func testHostBindingUsesCanonicalPathAndNeverStoresRawPath() {
    withDefaults {
      let alias = "/tmp/unused/../Llumi-UpdateCompletion-Fixture.app"
      let canonicalKeys = UpdateCompletionTracker.persistenceKeys(forHostPath: hostPath)!
      let aliasKeys = UpdateCompletionTracker.persistenceKeys(forHostPath: alias)!
      XCTAssertEqual(canonicalKeys.pending, aliasKeys.pending)
      XCTAssertEqual(canonicalKeys.acknowledged, aliasKeys.acknowledged)
      XCTAssertFalse(canonicalKeys.pending.contains(hostPath))
      XCTAssertEqual(canonicalKeys.pending.split(separator: ".").last?.count, 64)
      _ = prepare()
      XCTAssertNotNil(tracker("1.1.3", "3", hostPath: alias).completion)
    }
  }
  func testMalformedHostPathsCannotReadOrMutateAnyCompletionState() {
    withDefaults {
      _ = prepare()
      let pending = defaults.dictionary(forKey: pendingKey)! as NSDictionary
      for path in ["", "relative/Llumi.app", "/tmp/Llumi\n.app", "/" + String(repeating: "x", count: 4096)] {
        XCTAssertNil(UpdateCompletionTracker.persistenceKeys(forHostPath: path))
        let invalid = tracker("1.1.3", "3", hostPath: path)
        XCTAssertNil(invalid.completion)
        XCTAssertFalse(invalid.markInstallationIntent(targetVersion: "1.1.4", targetBuild: "4"))
        invalid.failedInstallation()
        invalid.acknowledgeCompletion()
        XCTAssertEqual(defaults.dictionary(forKey: pendingKey)! as NSDictionary, pending)
      }
    }
  }
}
