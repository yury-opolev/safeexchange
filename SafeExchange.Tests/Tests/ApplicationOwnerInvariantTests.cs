/// <summary>
/// ApplicationOwnerInvariantTests — pure-function tests of the ownership
/// invariant. These deliberately exercise ApplicationOwnerService.ValidateInvariant
/// in isolation (no DbContext) so the invariant logic is verified without a
/// Cosmos emulator dependency. End-to-end add/remove behaviour is covered by
/// integration tests in subsequent commits.
/// </summary>

namespace SafeExchange.Tests
{
    using NUnit.Framework;
    using SafeExchange.Core.Applications;
    using SafeExchange.Core.Model;
    using System;
    using System.Collections.Generic;

    [TestFixture]
    public class ApplicationOwnerInvariantTests
    {
        private const string AppId = "app-1";

        private static ApplicationOwner User(string upn)
            => new ApplicationOwner(AppId, OwnerSubjectType.User, upn, addedBy: "test");

        private static ApplicationOwner Group(string oid)
            => new ApplicationOwner(AppId, OwnerSubjectType.Group, oid, addedBy: "test");

        private static void Assert_PassesInvariant(params ApplicationOwner[] owners)
        {
            var sut = new ApplicationOwnerService(dbContext: null!);
            // Pure function — DbContext is unused. If we ever pull I/O into
            // ValidateInvariant this assertion will helpfully NRE and force a
            // re-think of the layering.
            Assert.DoesNotThrow(() => sut.ValidateInvariant(owners));
        }

        private static void Assert_FailsInvariant(string expectedMessageFragment, params ApplicationOwner[] owners)
        {
            var sut = new ApplicationOwnerService(dbContext: null!);
            var ex = Assert.Throws<ApplicationOwnerInvariantException>(() => sut.ValidateInvariant(owners));
            Assert.That(ex!.Message, Does.Contain(expectedMessageFragment));
        }

        [Test]
        public void Empty_owner_set_fails()
        {
            Assert_FailsInvariant("at least two distinct owner principals");
        }

        [Test]
        public void Single_user_fails()
        {
            Assert_FailsInvariant("at least two distinct owner principals", User("alice@contoso.com"));
        }

        [Test]
        public void Two_users_passes()
        {
            Assert_PassesInvariant(User("alice@contoso.com"), User("bob@contoso.com"));
        }

        [Test]
        public void User_plus_group_passes()
        {
            Assert_PassesInvariant(User("alice@contoso.com"), Group("group-oid-1"));
        }

        [Test]
        public void Two_groups_fails_because_no_user()
        {
            Assert_FailsInvariant("at least one User owner",
                Group("group-oid-1"), Group("group-oid-2"));
        }

        [Test]
        public void Duplicate_user_entries_count_as_one_principal_and_fail()
        {
            // Defensive: the EF composite key normally prevents duplicates, but the
            // pure check shouldn't be fooled if duplicates do reach it.
            Assert_FailsInvariant("at least two distinct owner principals",
                User("alice@contoso.com"), User("alice@contoso.com"));
        }
    }
}
