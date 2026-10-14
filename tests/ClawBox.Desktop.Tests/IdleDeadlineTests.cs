using ClawBox.Desktop.Services;

namespace ClawBox.Desktop.Tests;

public sealed class IdleDeadlineTests
{
    private sealed class Clock : TimeProvider
    {
        private long timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => timestamp;
        public void Advance(TimeSpan time) => timestamp += time.Ticks;
    }
    [Fact]
    public void Continuous_real_activity_can_exceed_thirty_minutes_and_only_silence_expires()
    {
        Clock clock = new(); using IdleDeadline deadline = new(TimeSpan.FromMinutes(30), clock);
        for (int i = 0; i < 5; i++)
        {
            clock.Advance(TimeSpan.FromMinutes(29)); deadline.Check();
            Assert.False(deadline.Source.IsCancellationRequested);
            deadline.Signal();
        }
        clock.Advance(TimeSpan.FromMinutes(29)); deadline.Check(); Assert.False(deadline.Source.IsCancellationRequested);
        clock.Advance(TimeSpan.FromMinutes(1)); deadline.Check(); Assert.True(deadline.Source.IsCancellationRequested);
        deadline.Signal(); Assert.True(deadline.Source.IsCancellationRequested);
    }
    [Fact]
    public void Manual_stop_does_not_resume_when_late_progress_arrives()
    {
        Clock clock = new(); using IdleDeadline deadline = new(TimeSpan.FromMinutes(30), clock);
        deadline.Source.Cancel(); deadline.Signal(); clock.Advance(TimeSpan.FromMinutes(1)); deadline.Check();
        Assert.True(deadline.Source.IsCancellationRequested);
    }
}
