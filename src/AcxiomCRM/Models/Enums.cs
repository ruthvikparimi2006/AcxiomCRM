using System.ComponentModel.DataAnnotations;

namespace AcxiomCRM.Models;

// Value sets from the project plan §16 #4. Stored as strings in the database.

public enum CustomerStatus { Active, Inactive }

public enum LeadStatus { New, Contacted, Qualified, Unqualified, Converted, Lost }

public enum LeadSource { Website, Referral, Campaign, [Display(Name = "Cold Call")] ColdCall, Event, Other }

public enum LeadPriority { Low, Medium, High }

public enum OpportunityStage { Qualification, Proposal, Negotiation, Won, Lost }

// Derived from Stage, never edited directly.
public enum OpportunityStatus { Open, Won, Lost }

// Also used as FollowUpType (§16 #4: FollowUpType = ActivityType).
public enum ActivityType { Call, Meeting, Email, Task }

public enum FollowUpStatus { Planned, Completed, Missed, Cancelled }

public enum ActivityStatus { Planned, Completed, Cancelled }
