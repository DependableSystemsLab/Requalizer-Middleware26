namespace OneOS.Runtime.Language.Models;

// Channel declarations of the node script language (NodeParser).
public enum ChannelDirection
{
    In,
    Out
}

public record ChannelDefinition(ChannelDirection Direction, string? Type, string Name);
