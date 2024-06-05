﻿// Copyright (c) SimpleIdServer. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using SimpleIdServer.IdServer.Api.Authorization.ResponseTypes;
using SimpleIdServer.IdServer.Domains;
using SimpleIdServer.IdServer.DTOs;
using SimpleIdServer.IdServer.Exceptions;
using SimpleIdServer.IdServer.Helpers;
using SimpleIdServer.IdServer.Jwt;
using SimpleIdServer.IdServer.Options;
using SimpleIdServer.IdServer.Resources;
using SimpleIdServer.IdServer.Stores;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SimpleIdServer.IdServer.Api.Authorization.Validators
{
    public class OAuthAuthorizationRequestValidator : IAuthorizationRequestValidator
    {
        private readonly IEnumerable<IResponseTypeHandler> _responseTypeHandlers;
        private readonly IUserHelper _userHelper;
        private readonly IClientRepository _clientRepository;
        private readonly IGrantHelper _grantHelper;
        private readonly IAmrHelper _amrHelper;
        private readonly IExtractRequestHelper _extractRequestHelper;
        private readonly IEnumerable<IOAuthResponseMode> _oauthResponseModes;
        private readonly IClientHelper _clientHelper;
        private readonly IJwtBuilder _jwtBuilder;
        private readonly IdServerHostOptions _options;

        public OAuthAuthorizationRequestValidator(
            IEnumerable<IResponseTypeHandler> responseTypeHandlers, 
            IUserHelper userHelper, 
            IClientRepository clientRepository, 
            IGrantHelper grantHelper, 
            IAmrHelper amrHelper, 
            IExtractRequestHelper extractRequestHelper, 
            IEnumerable<IOAuthResponseMode> oauthResponseModes, 
            IClientHelper clientHelper, 
            IJwtBuilder jwtBuilder, 
            IOptions<IdServerHostOptions> options)
        {
            _responseTypeHandlers = responseTypeHandlers;
            _userHelper = userHelper;
            _clientRepository = clientRepository;
            _grantHelper = grantHelper;
            _amrHelper = amrHelper;
            _extractRequestHelper = extractRequestHelper;
            _oauthResponseModes = oauthResponseModes;
            _clientHelper = clientHelper;
            _jwtBuilder = jwtBuilder;
            _options = options.Value;
        }

        public virtual Task<AuthorizationRequestValidationResult> ValidateAuthorizationRequest(HandlerContext context, CancellationToken cancellationToken)
        {
            var clientId = context.Request.RequestData.GetClientIdFromAuthorizationRequest();
            return ValidateAuthorizationRequest(context, clientId, cancellationToken);
        }

        public virtual async Task<AuthorizationRequestValidationResult> ValidateAuthorizationRequest(HandlerContext context, string clientId, CancellationToken cancellationToken)
        {
            context.SetClient(await AuthenticateClient(context.Realm, clientId, cancellationToken));
            await _extractRequestHelper.Extract(context);
            var requestedResponseTypes = context.Request.RequestData.GetResponseTypesFromAuthorizationRequest();
            if (!requestedResponseTypes.Any())
                throw new OAuthException(ErrorCodes.INVALID_REQUEST, string.Format(Global.MissingParameter, AuthorizationRequestParameters.ResponseType));

            var responseTypeHandlers = _responseTypeHandlers.Where(r => requestedResponseTypes.Contains(r.ResponseType));
            var unsupportedResponseType = requestedResponseTypes.Where(r => !_responseTypeHandlers.Any(rh => rh.ResponseType == r));
            if (unsupportedResponseType.Any())
                throw new OAuthException(ErrorCodes.UNSUPPORTED_RESPONSE_TYPE, string.Format(Global.MissingResponseTypes, string.Join(" ", unsupportedResponseType)));

            var scopes = context.Request.RequestData.GetScopesFromAuthorizationRequest();
            var authDetails = context.Request.RequestData.GetAuthorizationDetailsFromAuthorizationRequest();
            var resources = context.Request.RequestData.GetResourcesFromAuthorizationRequest();
            var grantRequest = await _grantHelper.Extract(context.Realm ?? Constants.DefaultRealm, scopes, resources, new List<string>(), authDetails, cancellationToken);

            if (!grantRequest.Scopes.Any() && !grantRequest.Audiences.Any() && !authDetails.Any())
                throw new OAuthException(ErrorCodes.INVALID_REQUEST, string.Format(Global.MissingParameters, $"{AuthorizationRequestParameters.Scope},{AuthorizationRequestParameters.Resource},{AuthorizationRequestParameters.AuthorizationDetails}"));

            var unsupportedScopes = grantRequest.Scopes.Where(s => s != Constants.StandardScopes.OpenIdScope.Name && !context.Client.Scopes.Any(sc => sc.Name == s));
            if (unsupportedScopes.Any())
                throw new OAuthException(ErrorCodes.INVALID_REQUEST, string.Format(Global.UnsupportedScopes, string.Join(",", unsupportedScopes)));

            if(authDetails != null && authDetails.Any(d => string.IsNullOrWhiteSpace(d.Type)))
                throw new OAuthException(ErrorCodes.INVALID_AUTHORIZATION_DETAILS, Global.AuthorizationDetailsTypeRequired);

            var unsupportedAuthorizationDetailsTypes = authDetails.Where(d => !context.Client.AuthorizationDataTypes.Contains(d.Type));
            if (unsupportedAuthorizationDetailsTypes.Any())
                throw new OAuthException(ErrorCodes.INVALID_AUTHORIZATION_DETAILS, string.Format(Global.UnsupportedAuthorizationDetailTypes, string.Join(",", unsupportedAuthorizationDetailsTypes.Select(t => t.Type))));

            OpenIdCredentialValidator.ValidateOpenIdCredential(authDetails);
            await CommonValidate(context, cancellationToken);
            var nonce = context.Request.RequestData.GetNonceFromAuthorizationRequest();
            var redirectUri = context.Request.RequestData.GetRedirectUriFromAuthorizationRequest();
            var responseTypes = context.Request.RequestData.GetResponseTypesFromAuthorizationRequest();
            if (string.IsNullOrWhiteSpace(redirectUri))
                throw new OAuthException(ErrorCodes.INVALID_REQUEST, string.Format(Global.MissingParameter, AuthorizationRequestParameters.RedirectUri));

            if (responseTypes.Contains(TokenResponseParameters.IdToken) && string.IsNullOrWhiteSpace(nonce))
                throw new OAuthException(ErrorCodes.INVALID_REQUEST, string.Format(Global.MissingParameter, AuthorizationRequestParameters.Nonce));

            if (context.Client.IsResourceParameterRequired && !resources.Any())
                throw new OAuthException(ErrorCodes.INVALID_TARGET, string.Format(Global.MissingParameter, AuthorizationRequestParameters.Resource));

            CheckGrantIdAndAction(context);
            return new AuthorizationRequestValidationResult(grantRequest, responseTypeHandlers);

            async Task CommonValidate(HandlerContext context, CancellationToken cancellationToken)
            {
                var client = context.Client;
                var redirectUri = context.Request.RequestData.GetRedirectUriFromAuthorizationRequest();
                var responseTypes = context.Request.RequestData.GetResponseTypesFromAuthorizationRequest();
                var responseMode = context.Request.RequestData.GetResponseModeFromAuthorizationRequest();
                var unsupportedResponseTypes = responseTypes.Where(t => !client.ResponseTypes.Contains(t));
                var redirectionUrls = await _clientHelper.GetRedirectionUrls(client, cancellationToken);
                if (!string.IsNullOrWhiteSpace(redirectUri) && !redirectionUrls.Any(r =>
                {
                    var regex = new Regex(r, (client.IsRedirectUrlCaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase));
                    return regex.Match(redirectUri).Success;
                })) throw new OAuthExceptionBadRequestURIException(redirectUri);

                if (!string.IsNullOrWhiteSpace(responseMode) && !_oauthResponseModes.Any(o => o.ResponseMode == responseMode)) throw new OAuthException(ErrorCodes.INVALID_REQUEST, string.Format(Global.BadResponseMode, responseMode));

                if (unsupportedResponseTypes.Any()) throw new OAuthException(ErrorCodes.UNSUPPORTED_RESPONSE_TYPE, string.Format(Global.BadResponseTypesClient, string.Join(",", unsupportedResponseTypes)));
            }

            async Task<Client> AuthenticateClient(string realm, string clientId, CancellationToken cancellationToken)
            {
                if (string.IsNullOrWhiteSpace(clientId))
                    throw new OAuthException(ErrorCodes.INVALID_REQUEST, string.Format(Global.MissingParameter, AuthorizationRequestParameters.ClientId));

                var client = await _clientRepository.GetByClientId(realm, clientId, cancellationToken);
                if (client == null)
                    throw new OAuthException(ErrorCodes.INVALID_CLIENT, string.Format(Global.UnknownClient, clientId));

                return client;
            }

            void CheckGrantIdAndAction(HandlerContext context)
            {
                var grantId = context.Request.RequestData.GetGrantIdFromAuthorizationRequest();
                var grantManagementAction = context.Request.RequestData.GetGrantManagementActionFromAuthorizationRequest();
                if (_options.GrantManagementActionRequired && string.IsNullOrWhiteSpace(grantManagementAction))
                    throw new OAuthException(ErrorCodes.INVALID_REQUEST, string.Format(Global.MissingParameter, AuthorizationRequestParameters.GrantManagementAction));

                if (!string.IsNullOrWhiteSpace(grantManagementAction) && !Constants.AllStandardGrantManagementActions.Contains(grantManagementAction))
                    throw new OAuthException(ErrorCodes.INVALID_REQUEST, string.Format(Global.InvalidGrantManagementAction, grantManagementAction));

                if (!context.IsComingFromConsentScreen() && !string.IsNullOrWhiteSpace(grantId) && grantManagementAction == Constants.StandardGrantManagementActions.Create)
                    throw new OAuthException(ErrorCodes.INVALID_REQUEST, Global.GrantIdCannotBeSpecified);

                if (!string.IsNullOrWhiteSpace(grantId) && string.IsNullOrWhiteSpace(grantManagementAction))
                    throw new OAuthException(ErrorCodes.INVALID_REQUEST, string.Format(Global.MissingParameter, AuthorizationRequestParameters.GrantManagementAction));
            }
        }

        public virtual async Task ValidateAuthorizationRequestWhenUserIsAuthenticated(GrantRequest request, HandlerContext context, CancellationToken cancellationToken)
        {
            var openidClient = context.Client;
            var authDetails = request.AuthorizationDetails;
            var scopes = request.Scopes;
            var clientId = context.Request.RequestData.GetClientIdFromAuthorizationRequest();
            var acrValues = context.Request.RequestData.GetAcrValuesFromAuthorizationRequest();
            var prompt = context.Request.RequestData.GetPromptFromAuthorizationRequest();
            var claims = context.Request.RequestData.GetClaimsFromAuthorizationRequest();
            if (context.User == null)
            {
                if (prompt == PromptParameters.None)
                    throw new OAuthException(ErrorCodes.LOGIN_REQUIRED, Global.LoginIsRequired);

                throw new OAuthLoginRequiredException(await GetFirstAmr(context.Realm, acrValues, claims, openidClient, cancellationToken));
            }

            var activeSession = context.Session;
            if (activeSession == null)
                throw new OAuthLoginRequiredException(await GetFirstAmr(context.Realm, acrValues, claims, openidClient, cancellationToken), true);

            var maxAge = context.Request.RequestData.GetMaxAgeFromAuthorizationRequest();
            var idTokenHint = context.Request.RequestData.GetIdTokenHintFromAuthorizationRequest();
            if (maxAge != null)
            {
                if (DateTime.UtcNow > activeSession.AuthenticationDateTime.AddSeconds(maxAge.Value))
                    throw new OAuthLoginRequiredException(await GetFirstAmr(context.Realm, acrValues, claims, openidClient, cancellationToken));
            }
            else if (openidClient.DefaultMaxAge != null && DateTime.UtcNow > activeSession.AuthenticationDateTime.AddSeconds(openidClient.DefaultMaxAge.Value))
                throw new OAuthLoginRequiredException(await GetFirstAmr(context.Realm, acrValues, claims, openidClient, cancellationToken));

            if (!string.IsNullOrWhiteSpace(idTokenHint))
            {
                var payload = ExtractIdTokenHint(context.Realm, idTokenHint);
                if (context.User.Name != payload.Subject)
                    throw new OAuthException(ErrorCodes.INVALID_REQUEST, Global.InvalidSubjectIdTokenHint);

                if (!payload.Audiences.Contains(context.GetIssuer()))
                    throw new OAuthException(ErrorCodes.INVALID_REQUEST, Global.InvalidAudienceIdTokenHint);
            }

            switch (prompt)
            {
                case PromptParameters.Login:
                    throw new OAuthLoginRequiredException(await GetFirstAmr(context.Realm, acrValues, claims, openidClient, cancellationToken));
                case PromptParameters.Consent:
                    RedirectToConsentView(context);
                    break;
                case PromptParameters.SelectAccount:
                    throw new OAuthSelectAccountRequiredException();
            }

            var grantManagementAction = context.Request.RequestData.GetGrantManagementActionFromAuthorizationRequest();
            if (string.IsNullOrWhiteSpace(grantManagementAction) && !_userHelper.HasOpenIDConsent(context.User, context.Realm, clientId, request, claims, authDetails))
            {
                RedirectToConsentView(context);
            }

            if (claims != null)
            {
                var idtokenClaims = claims.Where(cl => cl.Type == AuthorizationClaimTypes.IdToken && cl.IsEssential && Constants.AllUserClaims.Contains(cl.Name));
                var invalidClaims = idtokenClaims.Where(icl => !context.User.Claims.Any(cl => cl.Type == icl.Name && (icl.Values == null || !icl.Values.Any() || icl.Values.Contains(cl.Value))));
                if (invalidClaims.Any())
                    throw new OAuthException(ErrorCodes.INVALID_REQUEST, string.Format(Global.InvalidClaims, string.Join(",", invalidClaims.Select(i => i.Name))));
            }

            var acr = await _amrHelper.FetchDefaultAcr(context.Realm, acrValues, claims, openidClient, cancellationToken);
            var notAuthorizedAmrs = acr.AuthenticationMethodReferences.Where(c => !context.Request.Amrs.Contains(c));
            if (notAuthorizedAmrs.Any())
                throw new OAuthAuthenticatedUserAmrMissingException(acr.Name, notAuthorizedAmrs.First(), acr.AuthenticationMethodReferences);
        }

        protected virtual void RedirectToConsentView(HandlerContext context)
        {
            if (context.Client.IsConsentDisabled) return;
            throw new OAuthUserConsentRequiredException();
        }

        protected async Task<string> GetFirstAmr(string realm, IEnumerable<string> acrValues, IEnumerable<AuthorizedClaim> claims, Client client, CancellationToken cancellationToken)
        {
            var acr = await _amrHelper.FetchDefaultAcr(realm, acrValues, claims, client, cancellationToken);
            if (acr == null)
                return null;

            return acr.AuthenticationMethodReferences.First();
        }

        protected JsonWebToken ExtractIdTokenHint(string realm, string idTokenHint)
        {
            var extractionResult = _jwtBuilder.ReadSelfIssuedJsonWebToken(realm, idTokenHint);
            if (extractionResult.Error != null)
                throw new OAuthException(ErrorCodes.INVALID_REQUEST, extractionResult.Error);
            return extractionResult.Jwt;
        }
    }
}