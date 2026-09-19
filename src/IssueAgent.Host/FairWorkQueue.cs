using IssueAgent.Domain;

namespace IssueAgent.Host;

/// <summary>Stable identity used to deduplicate queued and in-flight work across poll cycles.</summary>
public readonly record struct WorkflowWorkKey(string Provider, string RepositoryId, long IssueNumber);

/// <summary>Admission priority. Lower values run first; control and reconciliation never wait
/// behind newly discovered planning work.</summary>
public enum WorkflowWorkPriority
{
    HumanCommand = 0,
    Reconciliation = 1,
    NewPlanning = 2,
}

public enum WorkflowCandidateKind
{
    NewPlanning,
    ExistingWorkflow,
}

/// <summary>One classified unit of workflow work. The callback performs exactly one reconciled
/// attempt; durable state remains in the provider and canonical comment.</summary>
public sealed record WorkflowCandidate(
    WorkflowWorkKey Key,
    WorkflowCandidateKind Kind,
    WorkflowWorkPriority Priority,
    WorkflowCommand? Command,
    long DiscoverySequence,
    Func<CancellationToken, Task> ExecuteAsync);

/// <summary>Thread-safe priority and round-robin admission with queued/in-flight deduplication.
/// FIFO is preserved within each repository and priority class.</summary>
public sealed class FairWorkAdmission
{
    private readonly object gate = new();
    private readonly SortedDictionary<WorkflowWorkPriority, PriorityBucket> buckets = [];
    private readonly Dictionary<WorkflowWorkKey, AdmissionState> admitted = [];

    public int Count
    {
        get
        {
            lock (gate) return admitted.Count;
        }
    }

    public int QueuedCount
    {
        get
        {
            lock (gate) return admitted.Count(pair => pair.Value == AdmissionState.Queued);
        }
    }

    public int InFlightCount
    {
        get
        {
            lock (gate) return admitted.Count(pair => pair.Value == AdmissionState.InFlight);
        }
    }

    public bool IsInFlight(WorkflowWorkKey key)
    {
        lock (gate) return admitted.TryGetValue(key, out var state) && state == AdmissionState.InFlight;
    }

    /// <summary>Returns false when the same provider/repository/issue is already queued or running.</summary>
    public bool TryEnqueue(WorkflowCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        lock (gate)
        {
            if (!admitted.TryAdd(candidate.Key, AdmissionState.Queued)) return false;
            if (!buckets.TryGetValue(candidate.Priority, out var bucket))
            {
                bucket = new PriorityBucket();
                buckets.Add(candidate.Priority, bucket);
            }
            bucket.Enqueue(candidate);
            return true;
        }
    }

    /// <summary>Dequeues the highest-priority candidate fairly and marks it in flight.</summary>
    public bool TryStart(out WorkflowCandidate? candidate)
    {
        lock (gate)
        {
            foreach (var bucket in buckets.Values)
            {
                if (!bucket.TryDequeue(out candidate)) continue;
                admitted[candidate!.Key] = AdmissionState.InFlight;
                return true;
            }
        }
        candidate = null;
        return false;
    }
    /// <summary>Removes work that has not started. In-flight attempts remain tracked.</summary>
    public int DiscardQueued()
    {
        lock (gate)
        {
            var queued = admitted
                .Where(pair => pair.Value == AdmissionState.Queued)
                .Select(pair => pair.Key)
                .ToArray();
            foreach (var key in queued) admitted.Remove(key);
            foreach (var bucket in buckets.Values) bucket.Clear();
            return queued.Length;
        }
    }


    /// <summary>Releases a completed or abandoned attempt so a later poll may reconcile it again.</summary>
    public void Complete(WorkflowWorkKey key)
    {
        lock (gate)
        {
            if (!admitted.Remove(key))
            {
                throw new InvalidOperationException($"Workflow work '{key}' is not admitted.");
            }
        }
    }

    private enum AdmissionState
    {
        Queued,
        InFlight,
    }

    private sealed class PriorityBucket
    {
        private readonly Dictionary<string, Queue<WorkflowCandidate>> repositories = new(StringComparer.Ordinal);
        private readonly List<string> order = [];
        private int cursor;

        public void Enqueue(WorkflowCandidate candidate)
        {
            if (!repositories.TryGetValue(candidate.Key.RepositoryId, out var queue))
            {
                queue = new Queue<WorkflowCandidate>();
                repositories.Add(candidate.Key.RepositoryId, queue);
                order.Add(candidate.Key.RepositoryId);
            }
            queue.Enqueue(candidate);
        }

        public bool TryDequeue(out WorkflowCandidate? candidate)
        {
            if (order.Count == 0)
            {
                candidate = null;
                return false;
            }

            for (var offset = 0; offset < order.Count; offset++)
            {
                var index = (cursor + offset) % order.Count;
                var repository = order[index];
                var queue = repositories[repository];
                if (queue.Count == 0) continue;
                candidate = queue.Dequeue();
                cursor = (index + 1) % order.Count;
                return true;
            }

            candidate = null;
            return false;
        }

        public void Clear()
        {
            repositories.Clear();
            order.Clear();
            cursor = 0;
        }
    }
}
