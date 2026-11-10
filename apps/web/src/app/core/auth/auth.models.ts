export interface EmpInfo {
  no: string;
  id: string;
  name: string;
  email?: string;
  department: string;
  orgShop: string;
  location: string;
  shops: string[];
  token: string;
  departmentCode: string;
  refreshToken: string;
}

export interface SsoUrls {
  loginPage: string;
  logoutPage: string;
}

export interface UserInfo {
  userId?: string;
  apName?: string;
  functionName?: string;
  shop?: string;
  no?: number;
  [key: string]: unknown;
}
