using Kinshout.Api.Data;
using Kinshout.Api.Dtos;
using Kinshout.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Kinshout.Api.Services;

public interface IUserBlockService
{
    Task BlockAsync(Guid blockerUserId, Guid blockedUserId, CancellationToken ct = default);
    Task UnblockAsync(Guid blockerUserId, Guid blockedUserId, CancellationToken ct = default);
    Task<PagedResultDto<UserBlockDto>> ListMineAsync(
        Guid blockerUserId,
        int page = 1,
        int pageSize = PagingHelper.DefaultPageSize,
        CancellationToken ct = default);
}

public class UserBlockService(KinshoutDbContext db) : IUserBlockService
{
    public async Task BlockAsync(Guid blockerUserId, Guid blockedUserId, CancellationToken ct = default)
    {
        if (blockerUserId == blockedUserId)
            throw new InvalidOperationException("Vous ne pouvez pas vous bloquer vous-même.");

        if (!await db.Users.AnyAsync(u => u.Id == blockedUserId, ct))
            throw new KeyNotFoundException("Utilisateur introuvable.");

        var exists = await db.UserBlocks.AnyAsync(
            b => b.BlockerUserId == blockerUserId && b.BlockedUserId == blockedUserId,
            ct);
        if (exists)
            return;

        db.UserBlocks.Add(new UserBlock
        {
            BlockerUserId = blockerUserId,
            BlockedUserId = blockedUserId,
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task UnblockAsync(Guid blockerUserId, Guid blockedUserId, CancellationToken ct = default)
    {
        var block = await db.UserBlocks.FirstOrDefaultAsync(
            b => b.BlockerUserId == blockerUserId && b.BlockedUserId == blockedUserId,
            ct);
        if (block is null)
            return;

        db.UserBlocks.Remove(block);
        await db.SaveChangesAsync(ct);
    }

    public async Task<PagedResultDto<UserBlockDto>> ListMineAsync(
        Guid blockerUserId,
        int page = 1,
        int pageSize = PagingHelper.DefaultPageSize,
        CancellationToken ct = default)
    {
        var (normalizedPage, normalizedPageSize) = PagingHelper.Normalize(page, pageSize);
        var query = db.UserBlocks
            .AsNoTracking()
            .Include(b => b.BlockedUser)
            .Where(b => b.BlockerUserId == blockerUserId)
            .OrderByDescending(b => b.CreatedAt);

        var total = await query.CountAsync(ct);
        var rows = await query
            .Skip((normalizedPage - 1) * normalizedPageSize)
            .Take(normalizedPageSize)
            .ToListAsync(ct);

        var items = rows
            .Select(b => new UserBlockDto(b.BlockedUserId, b.BlockedUser.DisplayName, b.CreatedAt))
            .ToList();

        return PagingHelper.Create(items, normalizedPage, normalizedPageSize, total);
    }
}
