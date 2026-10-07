namespace AcxiomCRM.Models;

public static class Roles
{
    public const string Admin = "Admin";
    public const string Manager = "Manager";
    public const string SalesExecutive = "SalesExecutive";

    public static readonly string[] All = [Admin, Manager, SalesExecutive];
}

// Authorization policy names, registered in Program.cs from the §7.1 permission matrix.
public static class Policies
{
    public const string Admin = "Admin";              // user writes, role management, permissions
    public const string ViewUsers = "ViewUsers";      // Admin; Manager (team, read-only) when Manager:CanViewTeamUsers
    public const string CrmUser = "CrmUser";          // any of the three roles; records are then scoped by ScopeService
    public const string ViewAuditLog = "ViewAuditLog"; // Admin; Manager (team, read-only) when Manager:CanViewAuditLog
}
