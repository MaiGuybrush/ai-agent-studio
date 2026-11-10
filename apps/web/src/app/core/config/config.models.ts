import { EmpInfo } from '../auth/auth.models';

export interface ModuleInfoConfig {
  moduleName: string;
  version?: string;
  owner: string;
  extNo: string;
  routePattern?: string;
}

export interface RuntimeConfig {
  appTitle: string;
  authApiUrl: string;
  mockAuth?: boolean;
  mockUser?: Partial<EmpInfo> | EmpInfo;
  modulesInfo?: ModuleInfoConfig[];
  [key: string]: unknown;
}
