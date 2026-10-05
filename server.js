const express=require('express'),fs=require('fs'),path=require('path'),QR=require('qrcode'),crypto=require('crypto');
const app=express();app.set('trust proxy',1);
const DIR=process.env.DATA_DIR||path.join(__dirname,'data');fs.mkdirSync(DIR,{recursive:true});
const F=path.join(DIR,'db.json'),SF=path.join(DIR,'sound.bin');
let db={settings:{title:'اختبار الدورة التدريبية',duration:10,count:10,pass:60,volume:0.5,sound:false,sv:0,mime:''},questions:[],results:[]};
try{const d=JSON.parse(fs.readFileSync(F));db.settings={...db.settings,...d.settings};db.questions=d.questions||[];db.results=d.results||[]}catch{}
const save=()=>fs.writeFileSync(F,JSON.stringify(db));
const PASS=process.env.ADMIN_PASSWORD||'admin123';
const attempts={},clients=new Set();
const feed=()=>`data:${JSON.stringify({r:db.results.slice(-200).reverse(),title:db.settings.title})}\n\n`;
const push=()=>clients.forEach(c=>c.write(feed()));
setInterval(()=>clients.forEach(c=>c.write(':\n\n')),25000);
app.use(express.json({limit:'1mb'}));
app.use(express.static(path.join(__dirname,'public')));
const auth=(q,s,n)=>q.get('x-pass')===PASS?n():s.status(401).json({error:'كلمة السر خاطئة'});
const num=(v,d,min,max)=>{v=Number(v);return isFinite(v)?Math.min(max,Math.max(min,v)):d};

app.get('/api/stream',(q,s)=>{s.set({'Content-Type':'text/event-stream','Cache-Control':'no-cache',Connection:'keep-alive'});s.flushHeaders();clients.add(s);s.write(feed());q.on('close',()=>clients.delete(s))});
app.get('/api/info',(q,s)=>{const t=db.settings;s.json({title:t.title,duration:t.duration,count:Math.min(t.count,db.questions.length),pass:t.pass,volume:t.volume,sound:t.sound,sv:t.sv})});
app.get('/api/qr',async(q,s)=>{const url=`${q.protocol}://${q.get('host')}/`;s.type('svg').send(await QR.toString(url,{type:'svg',margin:1}))});
app.get('/sound',(q,s)=>fs.existsSync(SF)?s.type(db.settings.mime||'audio/mpeg').send(fs.readFileSync(SF)):s.sendStatus(404));

app.post('/api/start',(q,s)=>{
  const name=String(q.body.name||'').trim().slice(0,60),branch=String(q.body.branch||'').trim().slice(0,60);
  if(!name||!branch)return s.status(400).json({error:'يرجى كتابة الاسم والفرع'});
  const st=db.settings,n=Math.min(st.count,db.questions.length);
  if(!n)return s.status(400).json({error:'لم تتم إضافة أسئلة بعد'});
  const qs=[...db.questions].sort(()=>Math.random()-.5).slice(0,n);
  const id=crypto.randomUUID();
  attempts[id]={name,branch,qs,end:Date.now()+st.duration*60000};
  s.json({id,end:attempts[id].end,now:Date.now(),qs:qs.map(x=>({q:x.q,o:x.o}))});
});
app.post('/api/submit',(q,s)=>{
  const a=attempts[q.body.id];if(!a)return s.status(400).json({error:'محاولة غير صالحة'});
  delete attempts[q.body.id];
  const ans=Date.now()>a.end+20000?[]:(q.body.answers||[]);
  const score=a.qs.reduce((t,x,i)=>t+(ans[i]===x.c?1:0),0),total=a.qs.length;
  const pct=Math.round(score/total*100),r={name:a.name,branch:a.branch,score,total,pct,pass:pct>=db.settings.pass,t:Date.now()};
  db.results.push(r);save();push();s.json(r);
});

const A=express.Router();A.use(auth);
A.get('/data',(q,s)=>s.json(db));
A.put('/settings',(q,s)=>{const b=q.body,t=db.settings;
  t.title=String(b.title||t.title).slice(0,100);t.duration=num(b.duration,t.duration,1,300);
  t.count=num(b.count,t.count,1,500);t.pass=num(b.pass,t.pass,0,100);t.volume=num(b.volume,t.volume,0,1);
  t.sound=!!b.sound&&fs.existsSync(SF);save();s.json(t)});
A.post('/question',(q,s)=>{const{q:text,o,c}=q.body;
  if(!text||!Array.isArray(o)||o.filter(x=>x&&x.trim()).length<2||!(c>=0&&c<o.length)||!o[c])return s.status(400).json({error:'بيانات السؤال ناقصة'});
  db.questions.push({id:crypto.randomUUID(),q:String(text).trim(),o:o.map(x=>String(x).trim()),c});save();s.json({ok:1})});
A.delete('/question/:id',(q,s)=>{db.questions=db.questions.filter(x=>x.id!==q.params.id);save();s.json({ok:1})});
A.delete('/results',(q,s)=>{db.results=[];save();push();s.json({ok:1})});
A.post('/sound',express.raw({type:'*/*',limit:'15mb'}),(q,s)=>{fs.writeFileSync(SF,q.body);
  db.settings.mime=q.get('content-type')||'audio/mpeg';db.settings.sound=true;db.settings.sv=Date.now();save();s.json({ok:1})});
A.delete('/sound',(q,s)=>{try{fs.unlinkSync(SF)}catch{}db.settings.sound=false;save();s.json({ok:1})});
app.use('/api/admin',A);
app.get('/admin',(q,s)=>s.sendFile(path.join(__dirname,'public','admin.html')));
app.get('/display',(q,s)=>s.sendFile(path.join(__dirname,'public','display.html')));
app.listen(process.env.PORT||3000,()=>console.log('running'));
