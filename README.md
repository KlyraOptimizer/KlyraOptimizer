<div align="center">

# ✦ Klyra Optimizer+ V1.4.1
### *Open Source Windows Optimizer • RGB Theme & Smart Anti-Conflict Engine*

[![License: MIT](https://img.shields.io/badge/License-MIT-7376F6.svg?style=for-the-badge)](https://opensource.org/licenses/MIT)
[![Platform: Windows 10 & 11](https://img.shields.io/badge/Windows-10%20%7C%2011%20(x64)-4B90F5?style=for-the-badge&logo=windows)](https://github.com)
[![Version: V1.4.1](https://shields.io)](https://github.com)
[![Author: @KlyraOptimizer](https://shields.io)](https://github.com)

<p align="center">
  O <strong>Klyra Optimizer+</strong> é um script <code>.bat</code> de código aberto para otimização do Windows 10 e 11 que divide as modificações em <strong>3 camadas claras de segurança</strong> (Standard, Medium e Avançado) e conta com um <strong>Motor Inteligente Anti-Conflito</strong> que verifica se outro otimizador já alterou seu sistema antes de aplicar qualquer ajuste.
</p>

</div>

---

## ✨ Diferenciais do Projeto

1. **🛡️ Motor Anti-Conflito (`:SAFE_REG`, `:SAFE_SC`, `:SAFE_BCD`)**:
   Diferente de scripts tradicionais que forçam comandos `reg add /f` e `sc config` cegamente, o Klyra Optimizer+ consulta o estado atual do Windows antes de cada passo:
   - Se a chave ou serviço já estiver otimizado (por execução anterior ou por outra ferramenta como *Chris Titus WinUtil*, *AtlasOS* ou *Platinum+*), ele exibe `[PULADO - JA ATIVO]` e evita conflitos.
   - Se ainda estiver no padrão lento do Windows, ele aplica a melhoria e contabiliza no relatório final.

2. **🔍 Scanner Pré-Execução Integrado (`Opção [5]`)**:
   Permite auditar o sistema antes de rodar qualquer otimização, detectando bloqueios de IFEO, estado de serviços de telemetria, HAGS, `Win32PrioritySeparation` e travas do Windows Update.

3. **🎨 Interface ANSI com Paleta RGB Gradiente**:
   Visual de terminal moderno com degradê em cores reais (TrueColor ANSI: Azul Royal $\rightarrow$ Índigo $\rightarrow$ Roxo $\rightarrow$ Rosa/Coral).

---

## 🚀 Os 3 Modos de Otimização (Acumulativos)

| Modo | Foco | O que está incluso |
| :--- | :--- | :--- |
| **`[1]` Standard** *(Padrão & Leve)* | Uso diário, notebooks e segurança máxima | • Criação automática de **Ponto de Restauração**<br>• Desativação de Telemetria, Copilot, Windows Recall e Bing Search<br>• Otimização de **GameDVR**, Fullscreen Optimizations (FSE) e **MMCSS** para jogos<br>• Ajustes seguros de **NTFS / FSUtil** e desativação de Hibernação/Fast Startup<br>• Limpeza profunda de arquivos temporários e caches de Shaders DX (NVIDIA/AMD) |
| **`[2]` Medium** *(Intermediário)* | Gamers que buscam menos processos e melhor rede | • **Tudo do Modo Standard +**<br>• Otimização da pilha **TCP/IP** e adaptadores de rede (**NIC**)<br>• Desativação verificada de serviços inúteis em 2º plano (`DiagTrack`, `SysMain`, `WerSvc`, etc.)<br>• **Debloat inteligente de AppX** (remove bloatware preservando **Paint** e **Captura de Tela**)<br>• Bloqueio de coletores de telemetria via **IFEO Debugger** (`CompatTelRunner.exe`) |
| **`[3]` Avançado** *(Extremo / Low Latency)* | FPS competitivo e redução de Input Lag | • **Tudo dos Modos Standard + Medium +**<br>• Ajustes de timers de boot via **BCDEdit** (`x2APIC`, `tscsyncpolicy enhanced`, limpeza de `useplatformclock`)<br>• **Memory Management & Kernel** (`DisablePagingExecutive`, `IoRing`, `BypassIO`)<br>• Ativação de **HAGS**, **MSI Mode** em barramentos PCI/USB e `Win32PrioritySeparation` (`0x26`)<br>• Desativação de **VBS / HVCI** (Isolamento de Núcleo)<br>• **Auto-detecção de CPU e GPU** com ajustes dedicados para **Intel**, **NVIDIA** e **AMD Radeon** |

---

## 🛠️ Ferramentas Open Source Integradas (`Opção [4]`)

O Klyra Optimizer+ inclui um menu rápido para executar ferramentas consagradas da comunidade sem sair do terminal:
- **Chris Titus Tech WinUtil** (via PowerShell oficial)
- **BleachBit** (verificação e instalação automatizada via `winget`)

---

## 📦 Como Baixar e Usar

1. Baixe a versão mais recente do arquivo **`KlyraOptimizer+.bat`** na aba [Releases](https://github.com) ou diretamente pelo repositório.
2. Execute o arquivo **`KlyraOptimizer+.bat`** (ele solicitará privilégios de Administrador automaticamente via UAC).
3. *(Opcional)* Pressione **`[5]`** para rodar o **Scanner de Conflitos** e verificar o estado atual do seu Windows.
4. Escolha entre os modos **`[1] Standard`**, **`[2] Medium`** ou **`[3] Avançado`**, configure suas preferências de *CompactOS*, *Windows Update*, *Microsoft Store* e *Bluetooth*, e aguarde a conclusão!

---

## 🤝 Créditos e Referências

- **Criador & Mantenedor**: [@KlyraOptimizer](https://github.com)
- **Repositório Oficial**: [://github.com](https://github.com)
- **Inspiração & Referência Técnica**: *Platinum+ Optimizer V9.5* (`@STEFANO83223` & `@Aledect`)
- **Utilitários Open Source**: *Chris Titus Tech WinUtil* & *BleachBit*

---

<div align="center">
  <sub>© 2026 Klyra Optimizer+. Distribuído sob a licença MIT.</sub><br>
  <sub>Este é um projeto independente e de código aberto, sem qualquer afiliação, endosso ou vínculo com a Microsoft Corporation.</sub>
</div>
