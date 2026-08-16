using TheCarl.Domain;

namespace TheCarl.UnitTests;

public class MoneyTests
{
    [Fact]
    public void MoneyFromGhanaianCedi_UsesGhsCurrency()
    {
        var money = Money.FromGhanaianCedi(123.45m);

        Assert.Equal("GHS", money.Currency);
        Assert.Equal(123.45m, money.Amount);
    }

    [Fact]
    public void Organization_CanContainBranchAndUsers()
    {
        var organization = new Organization
        {
            Name = "Test Organization",
            CurrencyCode = "GHS"
        };

        var branch = new Branch { Name = "Kumasi Central", OrganizationId = organization.Id };
        var user = new User { FullName = "Agent One", Email = "agent@example.com", OrganizationId = organization.Id };

        organization.Branches.Add(branch);
        organization.Users.Add(user);

        Assert.Single(organization.Branches);
        Assert.Single(organization.Users);
        Assert.Equal("Kumasi Central", organization.Branches[0].Name);
    }
}