using Chakki.Web.Data;
using Chakki.Web.Domain;
using Chakki.Web.Infrastructure;
using Chakki.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Chakki.Web.Controllers;

public record AttendanceRow(Employee Employee, Attendance? Record);

public class WorkforceController(AppDbContext db, OperationsService ops) : AppController
{
    [Authorize(Policy = Roles.People), HttpGet("/employees")]
    public async Task<IActionResult> Employees() => View(await db.Employees.OrderByDescending(e => e.IsActive).ThenBy(e => e.Name).ToListAsync());

    [Authorize(Policy = Roles.People), HttpPost("/employees/save"), ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveEmployee(int id, string name, string? phone, string? role, WageType wageType, decimal wageRate, DateOnly? joinedOn, bool isActive = true)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new BusinessRuleException("Name is required.");
        if (wageRate < 0) throw new BusinessRuleException("Wage cannot be negative.");
        Employee e;
        if (id == 0) { e = new Employee(); db.Employees.Add(e); }
        else e = await db.Employees.FindAsync(id) ?? throw new KeyNotFoundException();
        e.Name = name.Trim(); e.Phone = phone?.Trim(); e.Role = role?.Trim(); e.WageType = wageType; e.WageRate = wageRate;
        e.JoinedOn = joinedOn ?? (id == 0 ? Today : e.JoinedOn); e.IsActive = isActive;
        await db.SaveChangesAsync();
        Flash(T["Employee saved."]);
        return Redirect("/employees");
    }

    [Authorize(Policy = Roles.Floor), HttpGet("/attendance")]
    public async Task<IActionResult> Attendance(DateOnly? date)
    {
        var d = date ?? Today;
        var employees = await db.Employees.Where(e => e.IsActive).OrderBy(e => e.Name).ToListAsync();
        var records = await db.Attendances.Where(a => a.Date == d).ToDictionaryAsync(a => a.EmployeeId);
        ViewBag.Date = d;
        return View(employees.Select(e => new AttendanceRow(e, records.GetValueOrDefault(e.Id))).ToList());
    }

    [Authorize(Policy = Roles.Floor), HttpPost("/attendance"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Mark(int employeeId, DateOnly date, AttendanceStatus status, TimeOnly? checkIn, TimeOnly? checkOut)
    {
        var emp = await db.Employees.FindAsync(employeeId) ?? throw new KeyNotFoundException();
        if (date > Today) throw new BusinessRuleException("Attendance cannot be marked for a future date.");
        if (await db.PayrollRuns.AnyAsync(p => p.EmployeeId == emp.Id && p.PeriodStart <= date && p.PeriodEnd >= date))
            throw new BusinessRuleException("Wages for this day are already paid; attendance is locked.");
        var rec = await db.Attendances.FirstOrDefaultAsync(a => a.EmployeeId == emp.Id && a.Date == date);
        if (rec == null) { rec = new Attendance { EmployeeId = emp.Id, Date = date }; db.Attendances.Add(rec); }
        rec.Status = status;
        rec.CheckIn = status is AttendanceStatus.Present or AttendanceStatus.HalfDay ? checkIn ?? rec.CheckIn ?? TimeOnly.FromDateTime(DateTime.Now) : null;
        rec.CheckOut = checkOut ?? rec.CheckOut;
        await db.SaveChangesAsync();
        if (IsHtmx) return PartialView("_AttendanceRow", new AttendanceRow(emp, rec));
        return Redirect($"/attendance?date={date:yyyy-MM-dd}");
    }

    [Authorize(Policy = Roles.Money), HttpGet("/payroll")]
    public async Task<IActionResult> Payroll()
    {
        ViewBag.Employees = await db.Employees.Where(e => e.IsActive).OrderBy(e => e.Name).ToListAsync();
        return View(await db.PayrollRuns.Include(p => p.Employee).OrderByDescending(p => p.Id).Take(100).ToListAsync());
    }

    [Authorize(Policy = Roles.Money), HttpPost("/payroll"), ValidateAntiForgeryToken]
    public async Task<IActionResult> RunPayroll(int employeeId, DateOnly from, DateOnly to)
    {
        var run = await ops.RunPayrollAsync(employeeId, from, to, UserId);
        Flash(T["Wages of {0} for {1} days posted.", Fmt.Rs(run.Gross), Fmt.Qty(run.DaysWorked)]);
        return Redirect("/payroll");
    }

    [Authorize(Policy = Roles.Floor), HttpGet("/maintenance")]
    public async Task<IActionResult> Maintenance() => View(await db.MaintenanceTasks.OrderBy(m => m.Status).ThenBy(m => m.DueOn).Take(200).ToListAsync());

    [Authorize(Policy = Roles.Floor), HttpPost("/maintenance"), ValidateAntiForgeryToken]
    public async Task<IActionResult> AddMaintenance(string title, string kind, DateOnly dueOn, int? repeatDays)
    {
        if (string.IsNullOrWhiteSpace(title)) throw new BusinessRuleException("Title is required.");
        db.MaintenanceTasks.Add(new MaintenanceTask { Title = title.Trim(), Kind = kind, DueOn = dueOn == default ? Today : dueOn, RepeatDays = repeatDays is > 0 ? repeatDays : null });
        await db.SaveChangesAsync();
        Flash(T["Task scheduled."]);
        return Redirect("/maintenance");
    }

    [Authorize(Policy = Roles.Floor), HttpPost("/maintenance/{id:int}/done"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Done(int id, decimal cost, string? note)
    {
        await ops.CompleteMaintenanceAsync(id, cost, note, UserId);
        Flash(T["Task completed."]);
        return Redirect("/maintenance");
    }
}
