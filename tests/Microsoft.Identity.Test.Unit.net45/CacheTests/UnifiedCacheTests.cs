﻿// ------------------------------------------------------------------------------
// 
// Copyright (c) Microsoft Corporation.
// All rights reserved.
// 
// This code is licensed under the MIT License.
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files(the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and / or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions :
// 
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.
// 
// ------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.AppConfig;
using Microsoft.Identity.Client.Core;
using Microsoft.Identity.Client.Internal;
using Microsoft.Identity.Client.Cache;
using Microsoft.Identity.Client.UI;
using Microsoft.Identity.Test.Common.Core.Mocks;
using Microsoft.Identity.Test.Common.Mocks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Identity.Test.Common;

namespace Microsoft.Identity.Test.Unit.CacheTests
{
    [TestClass]
    public class UnifiedCacheTests
    {
        [TestInitialize]
        public void TestInitialize()
        {
            TestCommon.ResetStateAndInitMsal();
        }

#if !NET_CORE
        [TestMethod]
        [Description("Test unified token cache")]
        public void UnifiedCache_MsalStoresToAndReadRtFromAdalCache()
        {
            using (var harness = new MockHttpAndServiceBundle())
            {
                harness.HttpManager.AddInstanceDiscoveryMockHandler();

                var app = PublicClientApplicationBuilder.Create(MsalTestConstants.ClientId)
                                                        .AddKnownAuthority(new Uri(ClientApplicationBase.DefaultAuthority), true)
                                                        .WithUserTokenLegacyCachePersistenceForTest(
                                                            new TestLegacyCachePersistance())
                                                        .BuildConcrete();

                MsalMockHelpers.ConfigureMockWebUI(new AuthorizationResult(AuthorizationStatus.Success,
                                                                           app.RedirectUri + "?code=some-code"));
                harness.HttpManager.AddMockHandlerForTenantEndpointDiscovery(MsalTestConstants.AuthorityHomeTenant);
                harness.HttpManager.AddSuccessTokenResponseMockHandlerForPost(ClientApplicationBase.DefaultAuthority);

                AuthenticationResult result = app.AcquireTokenAsync(MsalTestConstants.Scope).Result;
                Assert.IsNotNull(result);

                // make sure Msal stored RT in Adal cache
                IDictionary<AdalTokenCacheKey, AdalResultWrapper> adalCacheDictionary =
                    AdalCacheOperations.Deserialize(harness.ServiceBundle.DefaultLogger, app.UserTokenCacheInternal.LegacyPersistence.LoadCache());

                Assert.IsTrue(adalCacheDictionary.Count == 1);

                var requestContext = RequestContext.CreateForTest();
                var accounts = app.UserTokenCacheInternal.GetAccounts(MsalTestConstants.AuthorityCommonTenant, requestContext);
                foreach (IAccount account in accounts)
                {
                    app.UserTokenCacheInternal.RemoveMsalAccount(account, requestContext);
                }

                harness.HttpManager.AddMockHandler(
                    new MockHttpMessageHandler()
                    {
                        Method = HttpMethod.Post,
                        PostData = new Dictionary<string, string>()
                        {
                            {"grant_type", "refresh_token"}
                        },
                        ResponseMessage = MockHelpers.CreateSuccessTokenResponseMessage(
                            MsalTestConstants.UniqueId,
                            MsalTestConstants.DisplayableId,
                            MsalTestConstants.Scope.ToArray())
                    });

                // Using RT from Adal cache for silent call
                AuthenticationResult result1 = app.AcquireTokenSilentAsync(
                    MsalTestConstants.Scope,
                    result.Account,
                    MsalTestConstants.AuthorityCommonTenant,
                    false).Result;

                Assert.IsNotNull(result1);
            }
        }

        [TestMethod]
        [Description("Test that RemoveAccount api is application specific")]
        public void UnifiedCache_RemoveAccountIsApplicationSpecific()
        {
            byte[] data = null;

            using (var harness = new MockHttpAndServiceBundle())
            {
                // login to app
                var app = PublicClientApplicationBuilder.Create(MsalTestConstants.ClientId)
                                                        .AddKnownAuthority(new Uri(ClientApplicationBase.DefaultAuthority), true)
                                                        .BuildConcrete();

                app.UserTokenCache.SetBeforeAccess((TokenCacheNotificationArgs args) =>
                {
                    args.TokenCache.Deserialize(data);
                });
                app.UserTokenCache.SetAfterAccess((TokenCacheNotificationArgs args) =>
                {
                    data = args.TokenCache.Serialize();
                });

                harness.HttpManager.AddInstanceDiscoveryMockHandler();

                MsalMockHelpers.ConfigureMockWebUI(new AuthorizationResult(AuthorizationStatus.Success,
                                                                           app.RedirectUri + "?code=some-code"));

                harness.HttpManager.AddMockHandlerForTenantEndpointDiscovery(MsalTestConstants.AuthorityHomeTenant);
                harness.HttpManager.AddSuccessTokenResponseMockHandlerForPost(ClientApplicationBase.DefaultAuthority);

                AuthenticationResult result = app.AcquireTokenAsync(MsalTestConstants.Scope).Result;
                Assert.IsNotNull(result);

                Assert.AreEqual(1, app.UserTokenCacheInternal.Accessor.GetAllAccountsAsString().Count);
                Assert.AreEqual(1, app.GetAccountsAsync().Result.Count());

                // login tp app1 with same credentials

                var app1 = PublicClientApplicationBuilder.Create(MsalTestConstants.ClientId_1)
                                                         .AddKnownAuthority(
                                                             new Uri(ClientApplicationBase.DefaultAuthority),
                                                             true).BuildConcrete();

                MsalMockHelpers.ConfigureMockWebUI(new AuthorizationResult(AuthorizationStatus.Success,
                                                                           app.RedirectUri + "?code=some-code"));

                app1.UserTokenCache.SetBeforeAccess((TokenCacheNotificationArgs args) =>
                {
                    args.TokenCache.Deserialize(data);
                });
                app1.UserTokenCache.SetAfterAccess((TokenCacheNotificationArgs args) =>
                {
                    data = args.TokenCache.Serialize();
                });
                
                harness.HttpManager.AddSuccessTokenResponseMockHandlerForPost(ClientApplicationBase.DefaultAuthority);

                result = app1.AcquireTokenAsync(MsalTestConstants.Scope).Result;
                Assert.IsNotNull(result);

                // make sure that only one account cache entity was created
                Assert.AreEqual(1, app1.UserTokenCacheInternal.Accessor.GetAllAccountsAsString().Count);
                Assert.AreEqual(1, app1.GetAccountsAsync().Result.Count());

                Assert.AreEqual(2, app1.UserTokenCacheInternal.Accessor.GetAllAccessTokensAsString().Count);
                Assert.AreEqual(2, app1.UserTokenCacheInternal.Accessor.GetAllRefreshTokensAsString().Count);
                Assert.AreEqual(2, app1.UserTokenCacheInternal.Accessor.GetAllIdTokensAsString().Count);

                // remove account from app
                app.RemoveAsync(app.GetAccountsAsync().Result.First()).Wait();

                // make sure account removed from app
                Assert.AreEqual(0, app.GetAccountsAsync().Result.Count());

                // make sure account Not removed from app1
                Assert.AreEqual(1, app1.GetAccountsAsync().Result.Count());
            }
        }
#endif

        [TestMethod]
        [Description("Test for duplicate key in ADAL cache")]
        public void UnifiedCache_ProcessAdalDictionaryForDuplicateKeyTest()
        {
            using (var harness = new MockHttpAndServiceBundle())
            {
                var app = PublicClientApplicationBuilder
                          .Create(MsalTestConstants.ClientId).AddKnownAuthority(new Uri(ClientApplicationBase.DefaultAuthority), true)
                          .WithUserTokenLegacyCachePersistenceForTest(new TestLegacyCachePersistance()).BuildConcrete();

                CreateAdalCache(harness.ServiceBundle.DefaultLogger, app.UserTokenCacheInternal.LegacyPersistence, MsalTestConstants.Scope.ToString());

                var adalUsers =
                    CacheFallbackOperations.GetAllAdalUsersForMsal(
                        harness.ServiceBundle.DefaultLogger,
                        app.UserTokenCacheInternal.LegacyPersistence,
                        MsalTestConstants.ClientId);

                CreateAdalCache(harness.ServiceBundle.DefaultLogger, app.UserTokenCacheInternal.LegacyPersistence, "user.read");

                var adalUsers2 =
                    CacheFallbackOperations.GetAllAdalUsersForMsal(
                        harness.ServiceBundle.DefaultLogger,
                        app.UserTokenCacheInternal.LegacyPersistence,
                        MsalTestConstants.ClientId);

                Assert.AreEqual(adalUsers.ClientInfoUsers.Keys.First(), adalUsers2.ClientInfoUsers.Keys.First());

                app.UserTokenCacheInternal.Accessor.ClearAccessTokens();
                app.UserTokenCacheInternal.Accessor.ClearRefreshTokens();
            }
        }

        private void CreateAdalCache(ICoreLogger logger, ILegacyCachePersistence legacyCachePersistence, string scopes)
        {
            var key = new AdalTokenCacheKey(
                MsalTestConstants.AuthorityHomeTenant,
                scopes,
                MsalTestConstants.ClientId,
                MsalTestConstants.TokenSubjectTypeUser,
                MsalTestConstants.UniqueId,
                MsalTestConstants.User.Username);

            var wrapper = new AdalResultWrapper()
            {
                Result = new AdalResult(null, null, DateTimeOffset.MinValue)
                {
                    UserInfo = new AdalUserInfo()
                    {
                        UniqueId = MsalTestConstants.UniqueId,
                        DisplayableId = MsalTestConstants.User.Username
                    }
                },
                RefreshToken = MsalTestConstants.ClientSecret,
                RawClientInfo = MsalTestConstants.RawClientId,
                ResourceInResponse = scopes
            };

            IDictionary<AdalTokenCacheKey, AdalResultWrapper> dictionary =
                AdalCacheOperations.Deserialize(logger, legacyCachePersistence.LoadCache());
            dictionary[key] = wrapper;
            legacyCachePersistence.WriteCache(AdalCacheOperations.Serialize(logger, dictionary));
        }
    }
}