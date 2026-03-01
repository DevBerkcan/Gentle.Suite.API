using GentleSuite.Application.DTOs;
using GentleSuite.Application.Interfaces;
using GentleSuite.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace GentleSuite.Infrastructure.Services;

public class UserServiceImpl : IUserService
{
    private readonly UserManager<AppUser> _um;
    public UserServiceImpl(UserManager<AppUser> um) { _um = um; }

    public async Task<List<UserListDto>> GetAllAsync(CancellationToken ct)
    {
        var users = await _um.Users.ToListAsync(ct);
        var result = new List<UserListDto>();
        foreach (var u in users)
        {
            var roles = await _um.GetRolesAsync(u);
            result.Add(new UserListDto(u.Id, u.Email!, u.FirstName, u.LastName, u.IsActive, roles.ToList()));
        }
        return result;
    }

    public async Task<UserListDto> CreateAsync(CreateUserRequest req, CancellationToken ct)
    {
        var user = new AppUser { UserName = req.Email, Email = req.Email, FirstName = req.FirstName, LastName = req.LastName, IsActive = true, EmailConfirmed = true };
        var result = await _um.CreateAsync(user, req.Password);
        if (!result.Succeeded) throw new InvalidOperationException(string.Join(", ", result.Errors.Select(e => e.Description)));
        if (req.Roles?.Any() == true) await _um.AddToRolesAsync(user, req.Roles);
        var roles = await _um.GetRolesAsync(user);
        return new UserListDto(user.Id, user.Email!, user.FirstName, user.LastName, user.IsActive, roles.ToList());
    }

    public async Task<UserListDto> UpdateAsync(string id, UpdateUserRequest req, CancellationToken ct)
    {
        var user = await _um.FindByIdAsync(id) ?? throw new KeyNotFoundException("Benutzer nicht gefunden");
        user.FirstName = req.FirstName; user.LastName = req.LastName; user.IsActive = req.IsActive;
        await _um.UpdateAsync(user);
        var currentRoles = await _um.GetRolesAsync(user);
        await _um.RemoveFromRolesAsync(user, currentRoles);
        if (req.Roles?.Any() == true) await _um.AddToRolesAsync(user, req.Roles);
        var roles = await _um.GetRolesAsync(user);
        return new UserListDto(user.Id, user.Email!, user.FirstName, user.LastName, user.IsActive, roles.ToList());
    }

    public async Task DeleteAsync(string id, CancellationToken ct)
    {
        var user = await _um.FindByIdAsync(id) ?? throw new KeyNotFoundException("Benutzer nicht gefunden");
        user.IsActive = false;
        await _um.UpdateAsync(user);
    }

    public async Task ResetPasswordAsync(string id, ResetPasswordRequest req, CancellationToken ct)
    {
        var user = await _um.FindByIdAsync(id) ?? throw new KeyNotFoundException("Benutzer nicht gefunden");
        var token = await _um.GeneratePasswordResetTokenAsync(user);
        var result = await _um.ResetPasswordAsync(user, token, req.NewPassword);
        if (!result.Succeeded) throw new InvalidOperationException(string.Join(", ", result.Errors.Select(e => e.Description)));
    }
}
