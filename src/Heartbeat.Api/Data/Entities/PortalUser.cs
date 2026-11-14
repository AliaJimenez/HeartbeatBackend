namespace Heartbeat.Api.Data.Entities;

public class PortalUser
{
    public Guid Id { get; set; }
    public string UserName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public Guid RoleId { get; set; }
    public bool IsActive { get; set; } = true;

    public Role Role { get; set; } = null!;
}