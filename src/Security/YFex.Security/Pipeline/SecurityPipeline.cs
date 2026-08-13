using System.Threading.Channels;
using YFex.Security.Events;

namespace YFex.Security.Pipeline;

public sealed class SecurityPipeline : ISecurityPipeline
{
    private readonly Channel<ISecurityEvent> _channel;

    public SecurityPipeline(int capacity = 10_000)
    {
        _channel = Channel.CreateBounded<ISecurityEvent>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = false,
            SingleWriter = false
        });
    }

    public ChannelWriter<ISecurityEvent> Writer => _channel.Writer;
    public ChannelReader<ISecurityEvent> Reader => _channel.Reader;
}
