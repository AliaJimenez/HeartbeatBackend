using Microsoft.AspNetCore.Identity;

namespace Heartbeat.Api.Data.Entities;

public enum AccountType
{
    Astronaut,
    Portal
}

public class ApplicationUser : IdentityUser<Guid>
{
    public AccountType AccountType { get; set; }
    public string DisplayName { get; set; } = string.Empty;
}