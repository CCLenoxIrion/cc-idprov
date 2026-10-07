using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Onboarding.Core.Directory;
using Onboarding.Core.Naming;
using Onboarding.Core.Security;
using Onboarding.Core.Workflow;
using Onboarding.Data;
using Onboarding.Data.Seed;
using Onboarding.Steps.Fakes;
using Onboarding.Tests.TestSupport;
using FakeDirectory = Onboarding.Steps.Fakes.FakeDirectory;
using Onboarding.Web.Services;

namespace Onboarding.Tests.Web;

/// <summary>Wires the web services against an in-memory SQLite database and fakes.</summary>
internal sealed class WebTestHost : IDisposable
{
    public static readonly Guid ManagerGuid = new("0f3c1a52-1b8e-4f5a-9c2d-000000000001");

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly TestAuthProvider _auth = new();

    public WebTestHost()
    {
        _connection.Open();
        Time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 5, 8, 0, 0, TimeSpan.Zero));
        CurrentUser = new CurrentUser(_auth);
        DbFactory = new TestDbFactory(_connection, CurrentUser, Time);
        using (var db = DbFactory.CreateDbContext())
        {
            db.Database.Migrate();
        }

        Directory = new FakeDirectory(new FakeDirectoryData
        {
            Objects =
            [
                new FakeDirectoryObject { ObjectGuid = ManagerGuid, DisplayName = "Petra Vogel", SamAccountName = "pvogel", TelephoneE164 = "+4974659296781" },
                new FakeDirectoryObject { DisplayName = "Martin Schmidt", SamAccountName = "mschmidt", Mail = "m.schmidt@cleancontrolling.de" },
            ],
            OrganizationalUnits = SeedData.Areas().Select(a => new OrganizationalUnit(a.OuDistinguishedName, a.OuCanonical)).ToList(),
            Licenses = [new LicenseAvailability("SPB", 10, 5), new LicenseAvailability("MCOEV", 5, 5)],
            PasswordPolicy = new PasswordPolicy(12, ComplexityEnabled: true),
        });
        Encryptor = new RecordingEncryptor();
        Requests = new RequestService(DbFactory, CurrentUser, new RequestWorkflow(Time), Directory, Directory, Directory, Directory, Encryptor, Time);
        Config = new ConfigService(DbFactory, CurrentUser, Directory);
    }

    public FakeTimeProvider Time { get; }
    public CurrentUser CurrentUser { get; }
    public TestDbFactory DbFactory { get; }
    public FakeDirectory Directory { get; }
    public RecordingEncryptor Encryptor { get; }
    public RequestService Requests { get; }
    public ConfigService Config { get; }

    public void SignIn(string name, params string[] roles) => _auth.SignIn(name, roles);

    public void SignInHr() => SignIn("hr.mueller", "Requester");

    public void SignInAdmin(string name = "it.admin1") => SignIn(name, "ITAdmin");

    /// <summary>Creates the "Vertrieb" department as admin and returns its id.</summary>
    public async Task<Guid> CreateDepartmentAsync(bool phoneExpected = true)
    {
        SignInAdmin();
        var department = TestConfig.Department();
        department.Id = Guid.NewGuid();
        department.Teams.PhoneExpected = phoneExpected;
        await Config.SaveDepartmentAsync(department);
        return department.Id;
    }

    public static Onboarding.Core.Domain.PersonInput Input(Guid departmentId, string first = "Lenox", string last = "Irion", string? mail = null, string? extension = "12") => new()
    {
        FirstName = first,
        LastName = last,
        Mail = mail ?? IdentityDeriver.SuggestMail(first, last, SeedData.Global()) ?? "",
        AreaId = SeedData.AreaTecSa,
        DepartmentId = departmentId,
        ManagerObjectGuid = ManagerGuid,
        EffectiveDate = new DateOnly(2026, 10, 7),
        Extension = extension,
    };

    public void Dispose() => _connection.Dispose();

    internal sealed class TestDbFactory(SqliteConnection connection, IActorAccessor actor, TimeProvider time) : IDbContextFactory<OnboardingDbContext>
    {
        public OnboardingDbContext CreateDbContext()
        {
            var builder = new DbContextOptionsBuilder<OnboardingDbContext>();
            builder.UseOnboardingSqlite(connection, actor, time);
            return new OnboardingDbContext(builder.Options);
        }
    }

    internal sealed class RecordingEncryptor : ISecretEncryptor
    {
        public int Calls { get; private set; }

        public byte[] Encrypt(SecretString secret)
        {
            Calls++;
            return [0xEE, (byte)secret.Length];
        }
    }

    private sealed class TestAuthProvider : AuthenticationStateProvider
    {
        private ClaimsPrincipal _user = new(new ClaimsIdentity());

        public void SignIn(string name, string[] roles)
        {
            var claims = new List<Claim> { new(ClaimTypes.Name, name) };
            claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
            _user = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        }

        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(_user));
    }
}
