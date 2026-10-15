const fs=require('fs'),assert=require('node:assert/strict');
const {chromium}=require('playwright');
(async()=>{
 const browser=await chromium.launch({channel:'msedge',headless:true});
 try{
 const page=await browser.newPage({viewport:{width:390,height:700}});
 const source=fs.readFileSync('src/FurtherAgent.Mobile/Transcript.cs','utf8');
 const shell=source.split('public const string Shell = """')[1].split('""";')[0];
 await page.setContent(shell);
 const result=await page.evaluate(async()=>{
 const tick=()=>new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r)));
 const section=(i,text)=>`<section id="m-${i}" style="min-height:300px"><article><p>${text}</p><pre style="overflow:auto;width:160px">${'long '.repeat(50)}</pre></article></section>`;
 let html=Array.from({length:8},(_,i)=>section(i,'message '+i)).join('')+'<aside><details id="session-files"><summary>Files</summary><p>file</p></details></aside>';
 renderTranscript(html,true,false);await tick();
 const first=document.getElementById('m-0'),paragraph=first.querySelector('p'),files=document.getElementById('session-files');files.open=true;
 const pre=first.querySelector('pre');pre.scrollLeft=30;
 scrollTo(0,350);await tick();const y=scrollY;
 renderTranscript(html.replace('message 7','stream changed'),false,false);await tick();
 const checks=[document.getElementById('m-0')===first,first.querySelector('p')===paragraph,files===document.getElementById('session-files')&&files.open,pre.scrollLeft===30,Math.abs(scrollY-y)<2,document.getElementById('m-7').textContent.includes('stream changed')];
 const anchor=document.getElementById('m-1'),top=anchor.getBoundingClientRect().top;
 renderTranscript(section(-1,'older')+html,false,true);await tick();
 checks.push(document.getElementById('m-1')===anchor,Math.abs(anchor.getBoundingClientRect().top-top)<2);
 renderTranscript(section(100,'new session'),true,false);await tick();checks.push(!document.getElementById('m-0'),!!document.getElementById('m-100'));
 renderTranscript(section(100,'new session').replace('<p>new session</p>','<h2>heading</h2>'),false,false);await tick();checks.push(!!document.querySelector('#m-100 h2'),!document.querySelector('#m-100 p'));
 renderTranscript(section(100,'new session')+section(101,'append'),false,false);await tick();checks.push(document.getElementById('chat').children.length===2,innerHeight+scrollY>=document.body.scrollHeight-2);
 return checks;
 });
 result.forEach((x,i)=>assert.equal(x,true,'DOM regression '+i));console.log('PASS '+result.length+' browser DOM checks');
 }finally{await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1});
