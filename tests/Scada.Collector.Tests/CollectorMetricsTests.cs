using System.Diagnostics.Metrics;
using Scada.Collector.Observability;
using Scada.Core.Enums;

namespace Scada.Collector.Tests;

public sealed class CollectorMetricsTests
{
    [Fact]
    public void PollInstrumentsAreVisibleToAMeterListener()
    {
        long polls = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meter) =>
        {
            if (instrument.Meter.Name == "Scada.Collector") meter.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
        {
            if (instrument.Name == "scada.collector.polls") Interlocked.Add(ref polls, value);
        });
        listener.Start();

        CollectorMetrics.Polled(ProtocolType.ModbusTcp, 12.5, 2);

        Assert.True(Interlocked.Read(ref polls) >= 1);
    }
}