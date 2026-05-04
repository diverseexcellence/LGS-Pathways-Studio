using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using LgsImpact.Api.Models;
using Microsoft.IdentityModel.Tokens;

namespace LgsImpact.Api.Services;

public interface ITokenService
{
    string GenerateToken(AdminDocument admin);
}

public class TokenService(IConfiguration config) : ITokenService
{
    public string GenerateToken(AdminDocument admin)
    {
        var secret = config["Jwt:Secret"] ?? throw new InvalidOperationException("JWT secret not configured");
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var expiry = DateTime.UtcNow.AddHours(double.Parse(config["Jwt:ExpiryHours"] ?? "8"));

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, admin.AdminId.ToString()),
            new Claim(JwtRegisteredClaimNames.Email, admin.Email),
            new Claim("name", admin.Name),
            new Claim("adminId", admin.AdminId.ToString()),
            new Claim("superAdmin", admin.IsSuperAdmin.ToString().ToLower()),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        var token = new JwtSecurityToken(
            issuer: config["Jwt:Issuer"],
            audience: config["Jwt:Audience"],
            claims: claims,
            expires: expiry,
            signingCredentials: creds
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
