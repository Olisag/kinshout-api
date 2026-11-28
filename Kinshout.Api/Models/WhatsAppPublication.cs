namespace Kinshout.Api.Models;

/// <summary>One WhatsApp channel slot. The idempotency key stops a second instance from sending the same bulletin.</summary>
public class WhatsAppPublication
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string IdempotencyKey { get; set; } = "";

    public string ChannelId { get; set; } = "";

    public string Category { get; set; } = "";

    /// <summary>retrieve or publish. Retrieval rows are never sent to WhatsApp.</summary>
    public string Purpose { get; set; } = "";

    public string Slot { get; set; } = "";

    /// <summary>Kinshasa calendar day, yyyy-MM-dd.</summary>
    public string LocalDate { get; set; } = "";

    public DateTime ScheduledAtUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public string? Body { get; set; }

    public Guid? DiscussionId { get; set; }

    public string? CanonicalUrl { get; set; }

    public string? ProviderName { get; set; }

    public string? ProviderReference { get; set; }

    public DateTime? ObservedAtUtc { get; set; }

    public string Status { get; set; } = WhatsAppPublicationStatus.Scheduled;

    public string? ProviderMessageId { get; set; }

    public DateTime? SubmittedAtUtc { get; set; }

    public DateTime? ConfirmedAtUtc { get; set; }

    public int RetryCount { get; set; }

    public string? LastError { get; set; }

    public bool Retryable { get; set; }

    public decimal? EstimatedCostUsd { get; set; }

    public bool Manual { get; set; }

    /// <summary>Internal provider trace. Never copied into the public message.</summary>
    public string? TraceJson { get; set; }
}

public static class WhatsAppPublicationStatus
{
    public const string Scheduled = "Scheduled";
    public const string Preparing = "Preparing";
    public const string Ready = "Ready";
    public const string Submitting = "Submitting";
    public const string Submitted = "Submitted";
    public const string Published = "Published";
    public const string ConfirmationUnknown = "ConfirmationUnknown";
    public const string Failed = "Failed";
    public const string Skipped = "Skipped";
    public const string Expired = "Expired";
    public const string Cancelled = "Cancelled";
}

public static class WhatsAppPublicationPurpose
{
    public const string Retrieve = "retrieve";
    public const string Publish = "publish";
}

/// <summary>One reserved external call. Shared by every API instance.</summary>
public class WhatsAppSpend
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Provider { get; set; } = "";

    public string SlotKey { get; set; } = "";

    public DateTime OccurredAtUtc { get; set; }

    public decimal EstimatedCostUsd { get; set; }

    public string Kind { get; set; } = "";
}

/// <summary>Shared pause switch. Configuration still has to be enabled before anything runs.</summary>
public class WhatsAppChannelControl
{
    public int Id { get; set; } = 1;

    public bool Paused { get; set; }
}
