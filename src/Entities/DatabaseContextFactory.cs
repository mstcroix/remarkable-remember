using System;
using System.IO;
using System.Linq;
using Microsoft.EntityFrameworkCore.Design;
using ReMarkableRemember.Helper;

namespace ReMarkableRemember.Entities;

#pragma warning disable CA1812

internal sealed class DatabaseContextFactory : IDesignTimeDbContextFactory<DatabaseContext>
{
    public DatabaseContext CreateDbContext(String[] args)
    {
        String dataSource = GetDataSource(args.FirstOrDefault());
        return new DatabaseContext(dataSource);
    }

    public static String GetDataSource(String? arg)
    {
        if (File.Exists(arg)) { return arg; }

        String dataSource = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), nameof(ReMarkableRemember), "database.db");
        FileSystem.CreateDirectory(dataSource);
        return dataSource;
    }
}

#pragma warning restore CA1812
