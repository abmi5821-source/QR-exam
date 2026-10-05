# منصة اختبار موظفي المصرف
- الشاشة الكبيرة: /display.html
- لوحة المدير: /admin.html  (كلمة السر من متغير ADMIN_PASSWORD)
- الموظفون: يمسحون QR فيفتح /
## النشر على Render
1. ارفع المجلد على GitHub.
2. Render > New Web Service > اختر المستودع. Build: `npm install` | Start: `npm start`
3. Environment: ADMIN_PASSWORD=كلمة_سرك
4. للحفاظ على الأسئلة عند إعادة التشغيل: أضف Disk بمسار /data وضع DATA_DIR=/data (الخطة المجانية تمسح الملفات).
