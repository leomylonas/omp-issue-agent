using System.Diagnostics.Metrics;

namespace IssueAgent.Observability.Tests;

/// <summary>Captures every measurement recorded on <see cref="IssueAgentMetrics.MeterName"/>
/// instruments for assertions, using the real <see cref="MeterListener"/> API rather than mocking.</summary>
internal sealed class MetricCapture : IDisposable
{
    private readonly MeterListener listener = new();
    private readonly List<(string Instrument, double Value)> measurements = [];

    public MetricCapture()
    {
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == IssueAgentMetrics.MeterName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, _, _) => Record(instrument.Name, value));
        listener.SetMeasurementEventCallback<double>((instrument, value, _, _) => Record(instrument.Name, value));
        listener.Start();
    }

    public IReadOnlyList<(string Instrument, double Value)> Measurements
    {
        get
        {
            lock (measurements) return measurements.ToArray();
        }
    }

    public double CountFor(string instrumentName)
    {
        lock (measurements) return measurements.Where(m => m.Instrument == instrumentName).Sum(m => m.Value);
    }

    private void Record(string instrumentName, double value)
    {
        lock (measurements)
        {
            measurements.Add((instrumentName, value));
        }
    }

    public void Dispose() => listener.Dispose();
}
