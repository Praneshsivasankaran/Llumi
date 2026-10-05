import Foundation

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
  private let publish: @Sendable ([ProviderID: UsageSnapshot]) -> Void
  init(
    sources: [ProviderID: any UsageSource], timeout: Double = 20,
    enabledProviders: Set<ProviderID> = Set(ProviderID.allCases),
    publish: @escaping @Sendable ([ProviderID: UsageSnapshot]) -> Void
  ) {
    self.sources = sources
    self.publish = publish
    self.timeout = timeout
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
      states[provider] = UsageSnapshot(provider: provider)
    }
    publish(states)
    for provider in added { refresh(provider) }
  }
  func refresh(_ provider: ProviderID? = nil, onlyIfOlderThan age: Double? = nil) {
    guard !stopped && !suspended else { return }
    for p in provider.map({ [$0] }) ?? ProviderID.allCases {
      guard enabledProviders.contains(p), flights[p] == nil, let source = sources[p] else { continue }
      if let age, let reading = states[p]?.reading, states[p]?.state == .live,
        Date().timeIntervalSince(reading.date) < age
      {
        continue
      }
      let timeout = self.timeout
      let revision = revisions[p, default: 0]
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
    }
  }
  private func complete(_ p: ProviderID, result: QueryResult, revision: Int) {
    flights.removeValue(forKey: p)
    guard !stopped && !suspended && enabledProviders.contains(p) else { return }
    // A disabled/re-enabled provider waits for cancellation cleanup before its next
    // query, preserving subprocess isolation without accepting the previous result.
    guard revision == revisions[p, default: 0] else { refresh(p); return }
    guard result.failure != .cancelled else { return }
    states[p]?.apply(result)
    publish(states)
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
    for provider in flights.keys { revisions[provider, default: 0] += 1 }
    let work = Array(flights.values)
    for t in work { t.cancel() }
    for t in work { await t.value }
  }
  func resume() {
    guard !stopped else { return }
    suspended = false
    refresh()
  }
  func stop() async {
    stopped = true
    await suspend()
  }
}
