using AcxiomCRM.Services;
using Microsoft.AspNetCore.Mvc;

namespace AcxiomCRM.ViewComponents;

// Header bell (GEN-01, FUP-05, D7): overdue and upcoming follow-ups in the signed-in user's scope.
public class NotificationsViewComponent(FollowUpService followUps) : ViewComponent
{
    public const int Shown = 5;

    public async Task<IViewComponentResult> InvokeAsync() => View(await followUps.RemindersAsync(take: Shown));
}
