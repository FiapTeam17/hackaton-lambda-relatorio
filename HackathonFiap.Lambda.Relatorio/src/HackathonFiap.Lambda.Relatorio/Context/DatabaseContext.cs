using Microsoft.EntityFrameworkCore;

namespace HackathonFiap.Lambda.Relatorio.Context;
public class DatabaseContext : DbContext
{
    public DatabaseContext(DbContextOptions<DatabaseContext> options) : base(options) { }
}