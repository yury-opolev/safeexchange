/// <summary>
/// RegistrarGuardTests — pure-function tests for the registrar protection
/// helpers added to SafeExchangeS2SApps. These guards back the rule
/// "the registrar (primary owner) can never be removed from owners — only
/// the whole app can be deleted." Endpoint-level behavior (RunRemoveOwner /
/// RunReplaceOwners returning 409) is integration-tested separately; here
/// we verify the predicates in isolation, no Cosmos / no DbContext.
/// </summary>

namespace SafeExchange.Tests
{
    using NUnit.Framework;
    using SafeExchange.Core.Functions;
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Model.Dto.Input;
    using System.Collections.Generic;

    [TestFixture]
    public class RegistrarGuardTests
    {
        private const string AppId = "app-1";
        private const string Registrar = "alice@contoso.com";

        private static Application AppWithRegistrar(string registrarUpn)
        {
            // "User {upn}" is the same shape SafeExchangeS2SApps.RunRegister
            // stamps onto Application.CreatedBy. Building it by hand here keeps
            // the test independent of the register code path.
            return new Application
            {
                Id = AppId,
                DisplayName = "TestApp",
                AadClientId = "00000000-0000-0000-0000-000000000001",
                AadTenantId = "00000000-0000-0000-0000-000000000002",
                ContactEmail = registrarUpn,
                CreatedBy = $"User {registrarUpn}",
            };
        }

        private static Application AppWithLegacyCreatedBy(string raw)
        {
            return new Application
            {
                Id = AppId,
                DisplayName = "TestApp",
                AadClientId = "00000000-0000-0000-0000-000000000001",
                AadTenantId = "00000000-0000-0000-0000-000000000002",
                ContactEmail = "x@contoso.com",
                CreatedBy = raw,
            };
        }

        private static ApplicationOwner UserOwner(string upn)
            => new ApplicationOwner(AppId, OwnerSubjectType.User, upn, addedBy: "test");

        private static ApplicationOwner GroupOwner(string oid)
            => new ApplicationOwner(AppId, OwnerSubjectType.Group, oid, addedBy: "test");

        // --- IsRegistrarRow ---

        [Test]
        public void IsRegistrarRow_true_for_user_matching_registrar_upn()
        {
            var app = AppWithRegistrar(Registrar);

            Assert.That(SafeExchangeS2SApps.IsRegistrarRow(app, OwnerSubjectType.User, Registrar), Is.True);
        }

        [Test]
        public void IsRegistrarRow_false_for_user_with_different_upn()
        {
            var app = AppWithRegistrar(Registrar);

            Assert.That(SafeExchangeS2SApps.IsRegistrarRow(app, OwnerSubjectType.User, "bob@contoso.com"), Is.False);
        }

        [Test]
        public void IsRegistrarRow_false_for_group_principal_even_if_id_matches()
        {
            // Groups can never be the registrar — Application.CreatedBy is set
            // from a user UPN on register. A group SubjectId that coincidentally
            // matches must not be protected.
            var app = AppWithRegistrar(Registrar);

            Assert.That(SafeExchangeS2SApps.IsRegistrarRow(app, OwnerSubjectType.Group, Registrar), Is.False);
        }

        [Test]
        public void IsRegistrarRow_user_match_is_case_insensitive()
        {
            // Entra UPNs are case-insensitive; the guard must mirror that or
            // the registrar could be "removed" by submitting a casing variant.
            var app = AppWithRegistrar("Alice@Contoso.com");

            Assert.That(SafeExchangeS2SApps.IsRegistrarRow(app, OwnerSubjectType.User, "alice@contoso.com"), Is.True);
        }

        [Test]
        public void IsRegistrarRow_false_when_app_has_no_parseable_registrar()
        {
            // Legacy / system-created rows that don't carry "User {upn}" return
            // false (the protection becomes a no-op rather than a hard 409).
            var app = AppWithLegacyCreatedBy("system");

            Assert.That(SafeExchangeS2SApps.IsRegistrarRow(app, OwnerSubjectType.User, "anyone@contoso.com"), Is.False);
        }

        // --- HasRegistrarOwner ---

        [Test]
        public void HasRegistrarOwner_true_when_registrar_present()
        {
            var app = AppWithRegistrar(Registrar);
            var desired = new List<ApplicationOwner>
            {
                UserOwner(Registrar),
                UserOwner("bob@contoso.com"),
            };

            Assert.That(SafeExchangeS2SApps.HasRegistrarOwner(app, desired), Is.True);
        }

        [Test]
        public void HasRegistrarOwner_false_when_registrar_missing()
        {
            // The exact scenario the guard protects against: an owner edit that
            // would leave the registrar off the list.
            var app = AppWithRegistrar(Registrar);
            var desired = new List<ApplicationOwner>
            {
                UserOwner("bob@contoso.com"),
                UserOwner("carol@contoso.com"),
            };

            Assert.That(SafeExchangeS2SApps.HasRegistrarOwner(app, desired), Is.False);
        }

        [Test]
        public void HasRegistrarOwner_false_when_registrar_only_present_as_group()
        {
            // Symmetric to IsRegistrarRow's group rule: a group row never
            // satisfies the registrar requirement, even if its SubjectId matches.
            var app = AppWithRegistrar(Registrar);
            var desired = new List<ApplicationOwner>
            {
                GroupOwner(Registrar),
                UserOwner("bob@contoso.com"),
            };

            Assert.That(SafeExchangeS2SApps.HasRegistrarOwner(app, desired), Is.False);
        }

        [Test]
        public void HasRegistrarOwner_matches_registrar_case_insensitively()
        {
            var app = AppWithRegistrar("Alice@Contoso.com");
            var desired = new List<ApplicationOwner>
            {
                UserOwner("alice@contoso.com"),
                UserOwner("bob@contoso.com"),
            };

            Assert.That(SafeExchangeS2SApps.HasRegistrarOwner(app, desired), Is.True);
        }

        [Test]
        public void HasRegistrarOwner_true_when_app_has_no_parseable_registrar()
        {
            // Legacy apps with non-"User {upn}" CreatedBy have nothing to protect
            // — the guard treats them as "no registrar set" so endpoints don't
            // 409 forever on data created before this rule existed.
            var app = AppWithLegacyCreatedBy("system");
            var desired = new List<ApplicationOwner>
            {
                UserOwner("anyone@contoso.com"),
                UserOwner("bob@contoso.com"),
            };

            Assert.That(SafeExchangeS2SApps.HasRegistrarOwner(app, desired), Is.True);
        }

        [Test]
        public void HasRegistrarOwner_true_when_app_createdBy_is_empty()
        {
            var app = AppWithLegacyCreatedBy(string.Empty);
            var desired = new List<ApplicationOwner>
            {
                UserOwner("bob@contoso.com"),
                UserOwner("carol@contoso.com"),
            };

            Assert.That(SafeExchangeS2SApps.HasRegistrarOwner(app, desired), Is.True);
        }
    }
}
