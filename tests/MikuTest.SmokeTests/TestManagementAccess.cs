using MikuTest.Web.Services;

sealed class TestManagementAccess(bool allowed = true) : IManagementAccess
{
    public Task<bool> CanAsync(string permission) => Task.FromResult(allowed);

    public Task<ManagementActor> RequireAsync(string permission) =>
        allowed
            ? Task.FromResult(new ManagementActor("test", "Test actor", "127.0.0.1"))
            : throw new InvalidOperationException("Denied");
}
