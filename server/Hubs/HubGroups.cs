namespace VisionSupport.Server.Hubs;

/// <summary>Nombres de grupo de SignalR compartidos entre el hub y los controladores REST.</summary>
public static class HubGroups
{
    public const string Panel = "panel";

    public static string Device(Guid deviceId) => $"device:{deviceId}";

    public static string Session(Guid sessionId) => $"session:{sessionId}";
}
