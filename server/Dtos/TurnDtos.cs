namespace VisionSupport.Server.Dtos;

/// <summary>
/// Forma compatible con <c>RTCIceServer</c> del navegador/WebRTC nativo:
/// <c>{ urls, username?, credential? }</c>. Las URLs <c>stun:</c> no necesitan credenciales
/// (se ignoran ahí); las <c>turn:</c>/<c>turns:</c> sí las usan.
/// </summary>
public record IceServerDto(string[] Urls, string? Username, string? Credential);

/// <summary>Respuesta de RemoteHub.GetTurnCredentials: uno o más iceServers y cuándo expiran.</summary>
public record TurnCredentialsDto(IceServerDto[] IceServers, DateTimeOffset ExpiresAt);
