# 两类凭证分开存：API 密钥存指纹，渠道密钥可解密

ApiKey 与 ChannelKey 的用途相反。ApiKey 只需要被校验，网关永远不需要读出明文；ChannelKey 必须原样发给上游，所以必须可解密。两个参考实现都把两者存成明文，库文件一旦泄露，所有上游账号同时失守。

所以 ApiKey 只存不可逆指纹并按指纹查找，完整值仅在创建时显示一次；ChannelKey 用 ASP.NET Core Data Protection 加密存储，keyring 放在数据目录。

## Consequences

- keyring 必须和数据库一起备份。只恢复数据库文件会导致所有 ChannelKey 解不开，只能逐个重新录入——这一条要写进部署文档。
- 用户丢失 ApiKey 明文后网关无法找回，只能重新创建。
