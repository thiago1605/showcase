using System.Linq.Expressions;
using FellowCore.Application.Common.Interfaces;
using Hangfire;

namespace FellowCore.Infrastructure.Jobs;

public class HangfireBackgroundJobs(IBackgroundJobClient client) : IBackgroundJobs
{
    public void Enqueue<T>(Expression<Func<T, Task>> methodCall)
    {
        client.Enqueue(methodCall);
    }
}
