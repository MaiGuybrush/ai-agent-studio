# SSO認證流程

提供舊專案程式供參考，必須符合此認證流程，寫法可以進行改寫或改善

1. docs\sso\app-init.service.ts.ref 網站初始流程，於main.ts 注入 APP_INITIALIZER
2. app-init.service.ts.ref 中處理認證服務為UserService, 其程式碼定義可參考user-service.ts.ref，此服務存在於package @inx/inx-auth-lib
3. app-init.service.ts.ref中使用ngrx 非必要，可使用signal或其它替代方案
4. MesApiCommonService 可取得fab, shop 等設定資訊
5. docs\sso\jwt-interceptor.ts.sample為http interceptor實作參考，可直接使用
