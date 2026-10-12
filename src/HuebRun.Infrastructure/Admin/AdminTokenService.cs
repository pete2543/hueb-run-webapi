using System.Security.Claims;
using System.Text;
using HuebRun.Domain;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace HuebRun.Infrastructure.Admin;

/// token มีแค่ id ผู้ใช้ + SecurityStamp; บทบาท/สิทธิ์งานโหลดจาก DB ทุก request (เปลี่ยนแล้วมีผลทันที)
public sealed class AdminTokenService(IOptions<AdminOptions> options, TimeProvider time)
{
    public const string StampClaim = "stamp";

    public static SymmetricSecurityKey SigningKey(AdminOptions o) => new(Encoding.UTF8.GetBytes(o.JwtSecret));

    public (string Token, DateTime ExpiresAt) Issue(AdminUser user)
    {
        var o = options.Value;
        var now = time.GetUtcNow().UtcDateTime;
        var expires = now.AddHours(o.TokenLifetimeHours);
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = o.Issuer,
            Audience = o.Issuer,
            IssuedAt = now,
            NotBefore = now,
            Expires = expires,
            Subject = new ClaimsIdentity([
                new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
                new Claim(StampClaim, user.SecurityStamp ?? ""),
            ]),
            SigningCredentials = new SigningCredentials(SigningKey(o), SecurityAlgorithms.HmacSha256),
        });
        return (token, expires);
    }
}
