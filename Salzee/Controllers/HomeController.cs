using System.Diagnostics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Salzee.Data;
using Salzee.Models;
using Salzee.Services;
using Salzee.ViewModels;

namespace Salzee.Controllers
{
    public class PropertyStatusResult
    {
        public string? Status { get; set; }
        public int Count { get; set; }
    }

    [Authorize]
    public class HomeController : Controller
    {
        private readonly ILogger<HomeController> _logger;
        private readonly PMSDbContext _context;
        private readonly IModulePermissionService _modulePermission;

        public HomeController(
            ILogger<HomeController> logger,
            PMSDbContext context,
            IModulePermissionService modulePermission)
        {
            _logger = logger;
            _context = context;
            _modulePermission = modulePermission;
        }

        [AllowAnonymous]
        public IActionResult Index()
        {
            return RedirectToAction("Login", "Account");
        }

        [HttpGet("/coditium")]
        public async Task<IActionResult> Coditium()
        {
            var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            var homePerm = await _modulePermission.GetPermissionAsync(userId, "Home");
            if (!_modulePermission.CanRead(homePerm))
            {
                return RedirectToAction("Workspace", "Home");
            }

            var currentUserType = await _context.Users
                .AsNoTracking()
                .Where(u => u.UserID == userId && u.IsActive)
                .Select(u => u.UserType)
                .FirstOrDefaultAsync();
            var showPendingTasks = string.Equals(currentUserType?.Trim(), "Admin", StringComparison.OrdinalIgnoreCase)
                || string.Equals(currentUserType?.Trim(), "Manager", StringComparison.OrdinalIgnoreCase);

            try
            {
                var today = DateTime.Now.Date;
                var sixMonthsAgo = DateTime.Now.AddMonths(-6);
                
                // Get property status distribution (handle missing tables)
                Dictionary<string, int> propertyStatusDict = new Dictionary<string, int>();
                try
                {
                    // Use raw SQL to avoid EF relationship validation
                    var propertyStatusData = await _context.Database
                        .SqlQueryRaw<PropertyStatusResult>("SELECT ISNULL(Status, 'Unknown') as Status, COUNT(*) as Count FROM Property GROUP BY Status")
                        .ToListAsync();
                    
                    propertyStatusDict = propertyStatusData.ToDictionary(x => x.Status ?? "Unknown", x => x.Count);
                }
                catch
                {
                    // Properties table doesn't exist or has relationship issues
                    propertyStatusDict = new Dictionary<string, int> { { "Unknown", 0 } };
                }

            // Get monthly payment data (latest available months from Payments table)
            Dictionary<string, decimal> monthlyPaymentsData = new Dictionary<string, decimal>();
            try
            {
                // Check if Payments table exists first using try-catch
                int tableExists = 0;
                try
                {
                    tableExists = await _context.Database
                        .SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'Payments'")
                        .FirstOrDefaultAsync();
                }
                catch
                {
                    // INFORMATION_SCHEMA query failed, assume table doesn't exist
                    tableExists = 0;
                }
                
                if (tableExists > 0)
                {
                    try
                    {
                        var monthlyPayments = await _context.Payments
                            .AsNoTracking()
                            .Where(p => p.Amount > 0)
                            .GroupBy(p => new { p.PaymentDate.Year, p.PaymentDate.Month })
                            .Select(g => new { 
                                Year = g.Key.Year, 
                                Month = g.Key.Month, 
                                Total = g.Sum(p => (decimal?)p.Amount) ?? 0 
                            })
                            .OrderByDescending(x => x.Year).ThenByDescending(x => x.Month)
                            .Take(6)
                            .ToListAsync();

                        monthlyPaymentsData = monthlyPayments
                            .OrderBy(x => x.Year).ThenBy(x => x.Month)
                            .ToDictionary(
                            x => new DateTime(x.Year, x.Month, 1).ToString("MMM yyyy"), 
                            x => x.Total);
                    }
                    catch
                    {
                        // Payments query failed
                    }
                }
            }
            catch
            {
                // Payments table doesn't exist or query failed
            }

            // Get payment status counts (handle missing PaymentSchedules/Payments tables)
            var overdue = 0;
            var dueThisWeek = 0;
            var upcoming = 0;
            var paid = 0;
            try
            {
                // Don't include Payments to avoid querying the Payments table
                var schedules = await _context.PaymentSchedules
                    .AsNoTracking()
                    .ToListAsync();
                
                // Try to load payments separately if table exists
                Dictionary<string, decimal> paymentTotals = new Dictionary<string, decimal>();
                try
                {
                    // Check if Payments table exists first using try-catch
                    int tableExists = 0;
                    try
                    {
                        tableExists = await _context.Database
                            .SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'Payments'")
                            .FirstOrDefaultAsync();
                    }
                    catch
                    {
                        // INFORMATION_SCHEMA query failed, assume table doesn't exist
                        tableExists = 0;
                    }
                    
                if (tableExists > 0)
                {
                    try
                    {
                        var payments = await _context.Payments
                            .AsNoTracking()
                            .Where(p => p.Status == "Completed")
                                .GroupBy(p => p.ScheduleID)
                                .Select(g => new { ScheduleID = g.Key, Total = g.Sum(p => (decimal?)p.Amount) ?? 0 })
                                .ToListAsync();
                            
                            paymentTotals = payments.Where(p => p.ScheduleID != null)
                                .ToDictionary(p => p.ScheduleID!, p => p.Total);
                        }
                        catch
                        {
                            // Payments query failed
                        }
                    }
                }
                catch
                {
                    // Payments table doesn't exist
                }
                
                overdue = schedules.Count(s => s.DueDate < today && 
                    (!paymentTotals.ContainsKey(s.ScheduleID) || paymentTotals[s.ScheduleID] < s.Amount));
                dueThisWeek = schedules.Count(s => s.DueDate >= today && s.DueDate <= today.AddDays(7) &&
                    (!paymentTotals.ContainsKey(s.ScheduleID) || paymentTotals[s.ScheduleID] < s.Amount));
                upcoming = schedules.Count(s => s.DueDate > today.AddDays(7) &&
                    (!paymentTotals.ContainsKey(s.ScheduleID) || paymentTotals[s.ScheduleID] < s.Amount));
                paid = schedules.Count(s => paymentTotals.ContainsKey(s.ScheduleID) && 
                    paymentTotals[s.ScheduleID] >= s.Amount);
            }
            catch
            {
                // PaymentSchedules table doesn't exist
            }

            // Get customer registration trend from Customers table (latest available months)
            Dictionary<string, int> customerTrendDict = new Dictionary<string, int>();
            try
            {
                var customerTrend = await _context.Customers
                    .AsNoTracking()
                    .GroupBy(c => new { c.CreatedAt.Year, c.CreatedAt.Month })
                    .Select(g => new { 
                        Year = g.Key.Year, 
                        Month = g.Key.Month, 
                        Count = (int?)g.Count() ?? 0 
                    })
                    .OrderByDescending(x => x.Year).ThenByDescending(x => x.Month)
                    .Take(6)
                    .ToListAsync();

                customerTrendDict = customerTrend
                    .OrderBy(x => x.Year).ThenBy(x => x.Month)
                    .ToDictionary(
                    x => new DateTime(x.Year, x.Month, 1).ToString("MMM yyyy"), 
                    x => x.Count);
            }
            catch
            {
                // Customers table doesn't exist
            }

            // Get dealer data for graph (handle missing tables)
            List<DealerDashboardData> dealerData = new List<DealerDashboardData>();
            try
            {
                // Load dealers without includes to avoid querying related tables that might not exist
                var dealers = await _context.Dealers
                    .AsNoTracking()
                    .ToListAsync();
                
                // Try to load related data separately
                Dictionary<int, int> dealerCustomerCounts = new Dictionary<int, int>();
                Dictionary<int, int> dealerPropertyCounts = new Dictionary<int, int>();
                
                try
                {
                    dealerCustomerCounts = await _context.Customers
                        .AsNoTracking()
                        .Where(c => c.DealerID.HasValue)
                        .GroupBy(c => c.DealerID!.Value)
                        .ToDictionaryAsync(g => g.Key, g => g.Count());
                }
                catch { }
                
                try
                {
                    dealerPropertyCounts = await _context.Properties
                        .AsNoTracking()
                        .Where(p => p.DealerID.HasValue)
                        .GroupBy(p => p.DealerID!.Value)
                        .ToDictionaryAsync(g => g.Key, g => g.Count());
                }
                catch { }

                // Sort in memory after loading
                var sortedDealers = dealers
                    .OrderByDescending(d => 
                    {
                        var dealerId = d.DealerID;
                        var customerCount = dealerCustomerCounts.ContainsKey(dealerId) ? dealerCustomerCounts[dealerId] : 0;
                        var propertyCount = dealerPropertyCounts.ContainsKey(dealerId) ? dealerPropertyCounts[dealerId] : 0;
                        return customerCount + propertyCount;
                    })
                    .Take(10)
                    .ToList();

                dealerData = sortedDealers.Select(d => 
                {
                    var dealerId = d.DealerID;
                    return new DealerDashboardData
                    {
                        DealershipName = d.DealershipName ?? string.Empty,
                        Customers = dealerCustomerCounts.ContainsKey(dealerId) ? dealerCustomerCounts[dealerId] : 0,
                        Properties = dealerPropertyCounts.ContainsKey(dealerId) ? dealerPropertyCounts[dealerId] : 0
                    };
                }).ToList();
            }
            catch
            {
                // Dealers table doesn't exist
            }

            // Get TotalPayments and RecentPayments
            decimal totalPayments = 0;
            List<Payment> recentPayments = new List<Payment>();
            try
            {
                totalPayments = await _context.Payments
                    .AsNoTracking()
                    .Select(p => (decimal?)p.Amount)
                    .SumAsync() ?? 0m;

                // Don't include related entities to avoid querying missing tables
                recentPayments = await _context.Payments
                    .AsNoTracking()
                    .OrderByDescending(p => p.PaymentDate)
                    .Take(5)
                    .ToListAsync();
            }
            catch
            {
                // Payments query failed; keep default values.
            }

            // Get dashboard counts (handle missing tables)
            int totalCustomers = 0;
            int pendingCustomers = 0;
            int pendingCustomerUpdateRequests = 0;
            int pendingWaivers = 0;
            int pendingTransfers = 0;
            int pendingRefunds = 0;
            int pendingAllotments = 0;
            string pendingWaiverStatusFilter = "Initiated";
            int totalProjects = 0;
            int totalProperties = 0;
            int availableProperties = 0;
            int allottedProperties = 0;
            List<Customer> recentCustomers = new List<Customer>();
            
            try
            {
                totalCustomers = await _context.Customers.AsNoTracking().CountAsync();
                pendingCustomers = await _context.Customers.AsNoTracking().CountAsync(c => c.Status == "Pending");
                pendingCustomerUpdateRequests = await _context.CustomerUpdateRequests
                    .AsNoTracking()
                    .CountAsync(r => r.Status == "Pending");
                totalProjects = await _context.Projects.AsNoTracking().CountAsync();
                totalProperties = await _context.Properties.AsNoTracking().CountAsync();
                availableProperties = await _context.Properties.AsNoTracking().CountAsync(p => p.Status == "Available");
                allottedProperties = await _context.Properties.AsNoTracking().CountAsync(p => p.Status == "Allotted");
                recentCustomers = await _context.Customers
                    .AsNoTracking()
                    .OrderByDescending(c => c.CreatedAt)
                    .Take(5)
                    .ToListAsync();

                var waiverStatuses = await _context.Configurations
                    .AsNoTracking()
                    .Where(c => c.ConfigKey == "WaiverWorkFlow")
                    .Select(c => c.ConfigValue)
                    .FirstOrDefaultAsync();
                var workflowStatuses = waiverStatuses?
                    .Split(',')
                    .Select(x => x.Trim())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .ToList() ?? new List<string>();
                if (workflowStatuses.Count > 0)
                {
                    pendingWaiverStatusFilter = workflowStatuses[0];
                }
                pendingWaivers = await _context.Waivers
                    .AsNoTracking()
                    .CountAsync(w => w.Status == pendingWaiverStatusFilter);

                pendingTransfers = await _context.Transfers
                    .AsNoTracking()
                    .CountAsync(t => !string.Equals(t.WorkFlowStatus, "Approved", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(t.WorkFlowStatus, "Declined", StringComparison.OrdinalIgnoreCase));

                pendingRefunds = await _context.Refunds
                    .AsNoTracking()
                    .CountAsync(r => !string.Equals(r.WorkflowStatus, "Approved", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(r.WorkflowStatus, "Declined", StringComparison.OrdinalIgnoreCase));

                pendingAllotments = await _context.Allotments
                    .AsNoTracking()
                    .CountAsync(a => !string.Equals(a.WorkFlowStatus, "Approved", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(a.WorkFlowStatus, "Declined", StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                // Tables don't exist
            }

                var dashboardData = new DashboardViewModel
                {
                    TotalCustomers = totalCustomers,
                    PendingCustomers = pendingCustomers,
                    PendingCustomerUpdateRequests = pendingCustomerUpdateRequests,
                    PendingWaivers = pendingWaivers,
                    PendingTransfers = pendingTransfers,
                    PendingRefunds = pendingRefunds,
                    PendingAllotments = pendingAllotments,
                    PendingWaiverStatusFilter = pendingWaiverStatusFilter,
                    ShowPendingTasks = showPendingTasks,
                    TotalProjects = totalProjects,
                    TotalProperties = totalProperties,
                    AvailableProperties = availableProperties,
                    AllottedProperties = allottedProperties,
                    TotalPayments = totalPayments,
                    RecentCustomers = recentCustomers,
                    RecentPayments = recentPayments,
                    
                    // Chart Data
                    PropertyStatusData = propertyStatusDict,
                    MonthlyPaymentsData = monthlyPaymentsData,
                    PaymentStatusData = new Dictionary<string, int>
                    {
                        { "Overdue", overdue },
                        { "Due This Week", dueThisWeek },
                        { "Upcoming", upcoming },
                        { "Paid", paid }
                    },
                    CustomerTrendData = customerTrendDict,
                    DealerData = dealerData
                };

                return View("Index", dashboardData);
            }
            catch
            {
                // If any database error occurs, return a minimal dashboard
                var dashboardData = new DashboardViewModel
                {
                    TotalCustomers = 0,
                    PendingCustomers = 0,
                    PendingCustomerUpdateRequests = 0,
                    PendingWaivers = 0,
                    PendingTransfers = 0,
                    PendingRefunds = 0,
                    PendingAllotments = 0,
                    PendingWaiverStatusFilter = "Initiated",
                    ShowPendingTasks = showPendingTasks,
                    TotalProjects = 0,
                    TotalProperties = 0,
                    AvailableProperties = 0,
                    AllottedProperties = 0,
                    TotalPayments = 0,
                    RecentCustomers = new List<Customer>(),
                    RecentPayments = new List<Payment>(),
                    PropertyStatusData = new Dictionary<string, int> { { "Unknown", 0 } },
                    MonthlyPaymentsData = new Dictionary<string, decimal>(),
                    PaymentStatusData = new Dictionary<string, int>
                    {
                        { "Overdue", 0 },
                        { "Due This Week", 0 },
                        { "Upcoming", 0 },
                        { "Paid", 0 }
                    },
                    CustomerTrendData = new Dictionary<string, int>(),
                    DealerData = new List<DealerDashboardData>()
                };
                return View("Index", dashboardData);
            }
        }

        public IActionResult Workspace()
        {
            return View();
        }

        [HttpGet("/Modules")]
        public IActionResult Modules()
        {
            var navigationService = HttpContext.RequestServices.GetRequiredService<INavigationService>();
            navigationService.TrySetActiveModule(HttpContext, NavigationModuleKeys.Operations);
            ViewData["Title"] = "Modules";
            return View();
        }

        public IActionResult Privacy()
        {
            return View();
        }

        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public IActionResult Error()
        {
            return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
        }
    }
}
