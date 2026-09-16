const {app,BrowserWindow}=require('electron');
const path=require('path');
function createWindow(){const w=new BrowserWindow({width:1600,height:950,minWidth:1200,minHeight:720,backgroundColor:'#07182d',webPreferences:{contextIsolation:true,sandbox:true}});w.loadFile('index.html');}
app.whenReady().then(()=>{createWindow();app.on('activate',()=>{if(BrowserWindow.getAllWindows().length===0)createWindow();});});
app.on('window-all-closed',()=>{if(process.platform!=='darwin')app.quit();});
