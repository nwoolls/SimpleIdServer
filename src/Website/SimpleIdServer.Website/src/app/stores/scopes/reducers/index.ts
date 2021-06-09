import { Action, createReducer, on } from '@ngrx/store';
import { SearchResult } from '../../applications/models/search.model';
import * as fromActions from '../actions/scope.actions';
import { OAuthScope } from '../models/oauthscope.model';

export interface SearchOAuthScopesState {
  Scopes: SearchResult<OAuthScope> | null;
}

export interface OAuthScopesState {
  Scopes: OAuthScope[] | null;
}

export const initialSearchOAuthScopesState: SearchOAuthScopesState = {
  Scopes: null
};

export const initialOAuthScopesState: OAuthScopesState = {
  Scopes: null
};

const searchOauthScopesReducer = createReducer(
  initialSearchOAuthScopesState,
  on(fromActions.completeSearch, (state, { content }) => {
    return {
      ...state,
      Scopes: { ...content }
    };
  })
);

const oauthScopesReducer = createReducer(
  initialOAuthScopesState,
  on(fromActions.completeGetAll, (state, { content }) => {
    return {
      ...state,
      Scopes: [ ...content ]
    };
  })
);

export function getSearchOauthScopesReducer(state: SearchOAuthScopesState | undefined, action: Action) {
  return searchOauthScopesReducer(state, action);
}

export function getOAuthScopesReducer(state: OAuthScopesState | undefined, action: Action) {
  return oauthScopesReducer(state, action);
}
