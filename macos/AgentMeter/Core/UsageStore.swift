import Foundation

enum UsageRefreshIntent: Sendable, Equatable { case automatic, userInitiated }
enum UsageRetryReason: Sendable, Equatable { case rateLimited, cooldown, manualCooldown }
struct UsageRefreshStatus: Sendable, Equatable {
  var checking = false
  var retryAt: Date?
  var retryReason: UsageRetryReason?
}

// Explicit checks can recover after installation or login without waiting for
// background error backoff. Provider rate limits and a separate click cadence
// remain mandatory across every entry point and provider toggle.
struct UsageRefreshPolicy: Sendable {
  private(set) var failureCount = 0
  private(set) var retryAfter: Date?
  private(set) var rateLimitAfter: Date?
  private(set) var manualRetryAfter: Date?
  private(set) var lastResetRefreshed: Date?
  private(set) var resetRefreshAfter: Date?

  mutating func record(_ result: QueryResult, at now: Date) {
    if let rateLimitAfter, rateLimitAfter <= now { self.rateLimitAfter = nil }
    if result.reading != nil {
      failureCount = 0
      retryAfter = nil
    } else if let failure = result.failure, failure != .cancelled {
      failureCount = min(failureCount + 1, 6)
      let initial: Double = failure == .rateLimited ? 60 : 30
      let delay = min(900, initial * pow(2, Double(failureCount - 1)))
      retryAfter = now.addingTimeInterval(delay)
      if failure == .rateLimited {
        rateLimitAfter = max(rateLimitAfter ?? now, now.addingTimeInterval(delay))
      }
    }
  }
  func blockedStatus(at now: Date, intent: UsageRefreshIntent) -> UsageRefreshStatus? {
    if let rateLimitAfter, rateLimitAfter > now {
      return .init(retryAt: rateLimitAfter, retryReason: .rateLimited)
    }
    // User controls that trigger an automatic path (toggle or wake) must not
    // circumvent the cadence established by a just-started explicit check.
    if let manualRetryAfter, manualRetryAfter > now {
      return .init(retryAt: manualRetryAfter, retryReason: .manualCooldown)
    }
    guard intent == .automatic else { return nil }
    let deadline = retryAfter
    guard let deadline, deadline > now else { return nil }
    return .init(retryAt: deadline, retryReason: .cooldown)
  }
  func allowsRefresh(at now: Date, intent: UsageRefreshIntent = .automatic) -> Bool {
    blockedStatus(at: now, intent: intent) == nil
  }
  func status(at now: Date) -> UsageRefreshStatus {
    if let rateLimitAfter, rateLimitAfter > now {
      return .init(retryAt: rateLimitAfter, retryReason: .rateLimited)
    }
    if let manualRetryAfter, manualRetryAfter > now {
      return .init(retryAt: manualRetryAfter, retryReason: .manualCooldown)
    }
    return blockedStatus(at: now, intent: .automatic) ?? .init()
  }
  mutating func didBeginRefresh(at now: Date, intent: UsageRefreshIntent) {
    if intent == .userInitiated {
      manualRetryAfter = now.addingTimeInterval(10)
      // Retire the old generic retry timer; completion establishes the new
      // automatic cadence if this explicit attempt also fails.
      retryAfter = nil
    }
  }
  func automaticRetryAt(at now: Date) -> Date? {
    [retryAfter, rateLimitAfter].compactMap { $0 }.filter { $0 > now }.max()
  }
  mutating func didRefreshReset(_ reset: Date, at now: Date) {
    lastResetRefreshed = reset
    recordResetAttempt(at: now)
  }
  mutating func recordResetAttempt(at now: Date) { resetRefreshAfter = now.addingTimeInterval(30) }
  mutating func clearResetHistory() { lastResetRefreshed = nil }
  func resetDeadline(for reset: Date, at now: Date) -> Date {
    max(max(reset.addingTimeInterval(1), automaticRetryAt(at: now) ?? now), resetRefreshAfter ?? now)
  }
  func nextReset(in snapshot: UsageSnapshot, at now: Date) -> Date? {
    snapshot.consumerWindows.compactMap { window in
      guard window.isSupported, window.used != nil, let reset = window.reset,
        reset.timeIntervalSince1970.isFinite, reset > now,
        reset.timeIntervalSince(now) <= 366 * 24 * 60 * 60,
        lastResetRefreshed.map({ reset > $0 }) ?? true
      else { return nil }
      return reset
    }.min()
  }
}

actor UsageStore {
  private let sources: [ProviderID: any UsageSource]
  private var flights: [ProviderID: Task<Void, Never>] = [:]
  private var revisions: [ProviderID: Int] = [:]
  private var enabledProviders: Set<ProviderID>
  private var states = Dictionary(
    uniqueKeysWithValues: ProviderID.allCases.map { ($0, UsageSnapshot(provider: $0)) })
  private var suspended = false
  private var stopped = false
  private let timeout: Double
  private let now: @Sendable () -> Date
  private let timerSleep: @Sendable (Double) async throws -> Void
  private var policies: [ProviderID: UsageRefreshPolicy] = [:]
  private var scheduled: [ProviderID: Task<Void, Never>] = [:]
  private var scheduledDeadlines: [ProviderID: Date] = [:]
  private var scheduledResets: [ProviderID: Date] = [:]
  private var timerRevisions: [ProviderID: Int] = [:]
  private var resetRefreshPending: Set<ProviderID> = []
  private var deferredRefresh: Set<ProviderID> = []
  private let publish: @Sendable ([ProviderID: UsageSnapshot]) -> Void
  init(
    sources: [ProviderID: any UsageSource], timeout: Double = 20,
    enabledProviders: Set<ProviderID> = Set(ProviderID.allCases),
    now: @escaping @Sendable () -> Date = { Date() },
    timerSleep: @escaping @Sendable (Double) async throws -> Void = {
      try await Task.sleep(for: .seconds($0))
    },
    publish: @escaping @Sendable ([ProviderID: UsageSnapshot]) -> Void
  ) {
    self.sources = sources
    self.publish = publish
    self.timeout = timeout
    self.now = now
    self.timerSleep = timerSleep
    self.enabledProviders = enabledProviders
  }
  func setEnabledProviders(_ enabled: Set<ProviderID>) {
    guard !stopped, enabled != enabledProviders else { return }
    let disabled = enabledProviders.subtracting(enabled)
    let added = enabled.subtracting(enabledProviders)
    enabledProviders = enabled
    for provider in disabled {
      revisions[provider, default: 0] += 1
      flights[provider]?.cancel()
      cancelSchedule(provider)
      // Retain only bounded retry/reset cadence, never account data. Rapid
      // disable/re-enable must not bypass a provider's rate-limit backoff.
      policies[provider]?.clearResetHistory()
      resetRefreshPending.remove(provider)
      deferredRefresh.remove(provider)
      states[provider] = UsageSnapshot(provider: provider)
    }
    for provider in added {
      states[provider]?.refreshStatus = policies[provider]?.status(at: now()) ?? .init()
    }
    publish(states)
    for provider in added { refresh(provider) }
  }
  func refresh(_ provider: ProviderID? = nil, onlyIfOlderThan age: Double? = nil,
    intent: UsageRefreshIntent = .automatic) {
    guard !stopped && !suspended else { return }
    for p in provider.map({ [$0] }) ?? ProviderID.allCases {
      guard enabledProviders.contains(p), flights[p] == nil, let source = sources[p] else { continue }
      let current = now()
      if let age, let reading = states[p]?.reading, states[p]?.state == .live,
        current.timeIntervalSince(reading.date) < age {
        // Activity that would skip a fresh reading is not a deferred request.
        continue
      }
      if let blocked = policies[p]?.blockedStatus(at: current, intent: intent) {
        // Present the effective explicit-retry restriction, even when this
        // request came from a background timer with a longer error cooldown.
        states[p]?.refreshStatus = policies[p]?.status(at: current) ?? .init()
        if intent == .automatic, blocked.retryReason == .manualCooldown,
          policies[p]?.automaticRetryAt(at: current) == nil {
          // Remember a real deferred request, rather than scheduling an extra
          // query after every successful manual check.
          deferredRefresh.insert(p)
        }
        publish(states)
        schedule(p)
        continue
      }
      let timeout = self.timeout
      let revision = revisions[p, default: 0]
      deferredRefresh.remove(p)
      policies[p, default: UsageRefreshPolicy()].didBeginRefresh(at: current, intent: intent)
      states[p]?.refreshStatus = .init(checking: true)
      publish(states)
      flights[p] = Task {
        let result = await withTaskGroup(of: QueryResult.self, returning: QueryResult.self) {
          group in
          group.addTask { await source.query() }
          group.addTask {
            do {
              try await Task.sleep(for: .seconds(timeout))
              return .fail(.timeout)
            } catch { return .fail(.cancelled) }
          }
          let result = await group.next() ?? .fail(.cancelled)
          group.cancelAll()
          return result
        }
        complete(p, result: result, revision: revision)
      }
      // Recompute after an explicit retry retires the old error deadline,
      // while retaining any reset timer needed during an in-flight query.
      schedule(p)
    }
  }
  private func complete(_ p: ProviderID, result: QueryResult, revision: Int) {
    flights.removeValue(forKey: p)
    guard !stopped && !suspended && enabledProviders.contains(p) else { return }
    // A disabled/re-enabled provider waits for cancellation cleanup before its next
    // query, preserving subprocess isolation without accepting the previous result.
    guard revision == revisions[p, default: 0] else { refresh(p); return }
    guard result.failure != .cancelled else {
      states[p]?.refreshStatus = policies[p]?.status(at: now()) ?? .init()
      publish(states)
      schedule(p)
      return
    }
    let oldBinding = states[p]?.reading?.binding
    states[p]?.apply(result)
    var policy = policies[p] ?? UsageRefreshPolicy()
    if oldBinding != states[p]?.reading?.binding { policy.clearResetHistory() }
    policy.record(result, at: now())
    let refreshAfterReset = resetRefreshPending.remove(p) != nil
    if refreshAfterReset { policy.recordResetAttempt(at: now()) }
    policies[p] = policy
    states[p]?.refreshStatus = policy.status(at: now())
    publish(states)
    schedule(p)
    if refreshAfterReset { refresh(p) }
  }
  private func cancelSchedule(_ provider: ProviderID) {
    timerRevisions[provider, default: 0] += 1
    scheduled.removeValue(forKey: provider)?.cancel()
    scheduledDeadlines.removeValue(forKey: provider)
    scheduledResets.removeValue(forKey: provider)
  }
  private func schedule(_ provider: ProviderID) {
    guard !stopped, !suspended, enabledProviders.contains(provider), sources[provider] != nil,
      let snapshot = states[provider]
    else { cancelSchedule(provider); return }
    let current = now()
    let policy = policies[provider] ?? UsageRefreshPolicy()
    let reset = policy.nextReset(in: snapshot, at: current)
    // A moving near-future reset cannot drive continuous successful queries.
    // Both reset cadence and retrieval backoff can delay the reported instant.
    let resetDeadline = reset.map { policy.resetDeadline(for: $0, at: current) }
    let retryDeadline = policy.automaticRetryAt(at: current)
    let deferredDeadline = deferredRefresh.contains(provider)
      ? policy.manualRetryAfter.flatMap { $0 > current ? $0 : nil } : nil
    guard let deadline = [resetDeadline, retryDeadline, deferredDeadline].compactMap({ $0 }).min() else {
      cancelSchedule(provider)
      return
    }
    guard scheduledDeadlines[provider] != deadline || scheduled[provider] == nil else { return }
    cancelSchedule(provider)
    scheduledDeadlines[provider] = deadline
    if deadline == resetDeadline { scheduledResets[provider] = reset }
    let revision = timerRevisions[provider, default: 0]
    let delay = max(0, deadline.timeIntervalSince(current))
    let sleep = timerSleep
    scheduled[provider] = Task { [weak self] in
      do { try await sleep(delay) } catch { return }
      guard !Task.isCancelled else { return }
      await self?.fireScheduled(provider, revision: revision)
    }
  }
  private func fireScheduled(_ provider: ProviderID, revision: Int) {
    guard timerRevisions[provider, default: 0] == revision,
      let deadline = scheduledDeadlines[provider]
    else { return }
    let reset = scheduledResets[provider]
    cancelSchedule(provider)
    guard !stopped, !suspended, enabledProviders.contains(provider) else { return }
    // Wall-clock adjustments may make a monotonic sleep wake early.
    guard now() >= deadline else { schedule(provider); return }
    if let reset {
      policies[provider, default: UsageRefreshPolicy()].didRefreshReset(reset, at: now())
      if flights[provider] != nil {
        // A query that began before the reset may contain pre-reset values.
        // Coalesce one follow-up after its owned subprocess is cleaned up.
        resetRefreshPending.insert(provider)
        return
      }
    }
    refresh(provider)
  }
  func snapshot() -> [ProviderID: UsageSnapshot] { states }
  func waitForIdle() async {
    while !flights.isEmpty {
      let work = Array(flights.values)
      for task in work { await task.value }
    }
  }
  func suspend() async {
    suspended = true
    resetRefreshPending.removeAll()
    deferredRefresh.removeAll()
    for provider in ProviderID.allCases { cancelSchedule(provider) }
    for provider in flights.keys { revisions[provider, default: 0] += 1 }
    for provider in enabledProviders {
      states[provider]?.refreshStatus = policies[provider]?.status(at: now()) ?? .init()
    }
    publish(states)
    let work = Array(flights.values)
    for t in work { t.cancel() }
    for t in work { await t.value }
  }
  func resume() {
    guard !stopped else { return }
    suspended = false
    refresh()
    for provider in enabledProviders { schedule(provider) }
  }
  func stop() async {
    stopped = true
    await suspend()
  }
}
