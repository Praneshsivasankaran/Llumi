using AgentMeter.Core;

namespace AgentMeter;

internal static class MonitorSelection
{
    internal static UsageWindow[] Details(ProviderState state) => UsagePresentation.Windows(state);
    internal static UsageWindow? Select(ProviderState state) => UsagePresentation.Primary(state);
}
