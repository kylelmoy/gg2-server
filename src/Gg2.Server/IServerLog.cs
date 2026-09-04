namespace Gg2.Server;

/// <summary>Where the server reports what it did. Nothing here is on the wire.</summary>
public interface IServerLog
{
    void Info(string message);
}
