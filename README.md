# منصة اختبار موظفي مصرف الرافدين (ASP.NET Core + PostgreSQL)
- `/` صفحة الموظف | `/display` الشاشة الكبيرة | `/admin` لوحة المدير
## Render
1. أنشئ PostgreSQL على Render وانسخ Internal Database URL.
2. ارفع المجلد إلى GitHub، ثم New > Web Service > Runtime: Docker.
3. Environment: DATABASE_URL (رابط القاعدة) , ADMIN_USER , ADMIN_PASSWORD
