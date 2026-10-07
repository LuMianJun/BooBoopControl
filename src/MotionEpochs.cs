namespace BooBoopBridge;

// Pure queue bookkeeping, always guarded by the controller's existing lock.
// No protocol, native host, or device is involved; linked directly into mock tests.
internal readonly record struct MotionTicket(string Mode, long Global, long Channel);
internal sealed class MotionEpochs
{
    private long _global, _stretch, _vibration;
    public MotionTicket Capture(string mode, bool stop)
    {
        if (mode != "aa01" && mode != "bb01")
            throw new System.ArgumentOutOfRangeException(nameof(mode));
        if (stop)
        {
            if (mode == "aa01") ++_stretch;
            else ++_vibration;
        }
        return new MotionTicket(mode, _global, mode == "aa01" ? _stretch : _vibration);
    }
    public bool IsCurrent(MotionTicket ticket) => ticket.Global == _global &&
        ticket.Channel == (ticket.Mode == "aa01" ? _stretch : _vibration);
    public void StopAll() => ++_global;
}
