using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using VerbellaCMG.CaseManagement.Api.Data;
using VerbellaCMG.CaseManagement.Api.Models.Entities;

var connectionString = "Server=WIN-SQL002\\MSSQLSERVER01;Database=VerbellaCMG_CaseManagement;Trusted_Connection=True;TrustServerCertificate=True";

var options = new DbContextOptionsBuilder<AppDbContext>()
    .UseSqlServer(connectionString)
    .Options;

using var context = new AppDbContext(options);
var userStore = new Microsoft.AspNetCore.Identity.EntityFrameworkCore.UserStore<ApplicationUser>(context);
var userManager = new UserManager<ApplicationUser>(
    userStore, null, new PasswordHasher<ApplicationUser>(), null, null, null, null, null, null);

var adminEmail = "pkailas@verbella.com";
var adminPassword = "Admin123!";

var existing = await userManager.FindByEmailAsync(adminEmail);
if (existing != null)
{
    Console.WriteLine($"User {adminEmail} already exists (Id: {existing.Id})");
    return;
}

var user = new ApplicationUser
{
    UserName = adminEmail,
    Email = adminEmail,
    EmailConfirmed = true
};

var result = await userManager.CreateAsync(user, adminPassword);
if (result.Succeeded)
{
    await userManager.AddToRoleAsync(user, "Admin");
    Console.WriteLine($"Created {adminEmail} with Admin role");
}
else
{
    Console.WriteLine($"FAILED: {string.Join(", ", result.Errors.Select(e => e.Description))}");
}
