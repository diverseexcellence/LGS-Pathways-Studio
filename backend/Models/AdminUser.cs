namespace LgsImpact.Api.Models;

public class AdminUser
{
    public int AdminId { get; set; }
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public DateTime? LastLogin { get; set; }
    public bool IsActive { get; set; } = true;
}
