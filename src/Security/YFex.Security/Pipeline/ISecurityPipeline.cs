using System.Threading.Channels;
using YFex.Security.Events;

namespace YFex.Security.Pipeline;

public interface ISecurityPipeline
{
    ChannelWriter<ISecurityEvent> Writer { get; }
    ChannelReader<ISecurityEvent> Reader { get; }
}
