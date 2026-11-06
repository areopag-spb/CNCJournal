using remeLog.Core;
using remeLog.Core.Extensions;
using remeLog.Infrastructure.Types;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;

namespace remeLog.Models
{
    public class Part : ObservableObject, IDataErrorInfo
    {
        public Part(
            Guid guid,
            string machine,
            string shift,
            DateTime shiftDate,
            string @operator,
            string partName,
            string order,
            int setup,
            double finishedCount,
            int defectiveCount,
            int totalCount,
            DateTime startSetupTime,
            DateTime startMachiningTime,
            double setupTimeFact,
            DateTime endMachiningTime,
            double setupTimePlan,
            double setupTimePlanForReport,
            double singleProductionTimePlan,
            double productionTimeFact, 
            TimeSpan machiningTime,
            double setupDowntimes,
            double machiningDowntimes,
            double partialSetupTime,
            double createNcProgramTime,
            double maintenanceTime,
            double toolSearchingTime,
            double toolChangingTime,
            double mentoringTime,
            double contactingDepartmentsTime,
            double fixtureMakingTime,
            double hardwareFailureTime,
            double specialDowntimeTime,
            string operatorComment,
            string masterSetupComment = "",
            string masterMachiningComment = "",
            string specifiedDowntimesComment = "",
            string unspecifiedDowntimesComment = "",
            string masterComment = "",
            string masterSetupDetail = "",
            string masterMachiningDetail = "",
            double fixedSetupTimePlan = 0,
            double fixedMachineTimePlan = 0,
            string engineerConclusion = "",
            bool excludeFromReports = false,
            string engineerComment = "",
            string longSetupReasonComment = "",
            string longSetupFixComment = "",
            string longSetupEngeneerComment = "",
            double excludedOperationsTime = 0,
            string increaseReason = "",
            string setupReasonOverride = "",
            string setupReasonOverrideComment = "",
            bool setupReasonOverrideIsMasterFault = false,
            string setupReasonOverrideMasterFaultComment = "",
            string machiningReasonOverride = "",
            string machiningReasonOverrideComment = "",
            bool machiningReasonOverrideIsMasterFault = false,
            string machiningReasonOverrideMasterFaultComment = "",
            string reasonOverrideBy = "",
            DateTime? reasonOverrideAt = null
            )
        {
            _Guid = guid;
            _Machine = machine;
            _Shift = shift;
            _ShiftDate = shiftDate;
            _Operator = @operator;
            _PartName = partName;
            _Order = order;
            _Setup = setup;
            _FinishedCount = finishedCount;
            _DefectiveCount = defectiveCount;
            _TotalCount = totalCount;
            _StartSetupTime = startSetupTime;
            _StartMachiningTime = startMachiningTime;
            _EndMachiningTime = endMachiningTime;
            _SetupTimePlan = setupTimePlan;
            _SetupTimePlanForReport = setupTimePlanForReport;
            _SingleProductionTimePlan = singleProductionTimePlan;
            _MachiningTime = machiningTime;
            _SetupDowntimes = setupDowntimes;
            _MachiningDowntimes = machiningDowntimes;
            _PartialSetupTime = partialSetupTime;
            _CreateNcProgramTime = createNcProgramTime;
            _MaintenanceTime = maintenanceTime;
            _ToolSearchingTime = toolSearchingTime;
            _ToolChangingTime = toolChangingTime;
            _MentoringTime = mentoringTime;
            _ContactingDepartmentsTime = contactingDepartmentsTime;
            _FixtureMakingTime = fixtureMakingTime;
            _HardwareFailureTime = hardwareFailureTime;
            _SpecialDowntimeTime = specialDowntimeTime;
            _OperatorComment = operatorComment;
            _MasterSetupComment = masterSetupComment;
            _MasterMachiningComment = masterMachiningComment;
            _SpecifiedDowntimesComment = specifiedDowntimesComment;
            _UnspecifiedDowntimesComment = unspecifiedDowntimesComment;
            _MasterComment = masterComment;
            _MasterSetupDetail = masterSetupDetail;
            _MasterMachiningDetail = masterMachiningDetail;
            _FixedSetupTimePlan = fixedSetupTimePlan;
            _FixedProductionTimePlan = fixedMachineTimePlan;
            _EngineerConclusion = engineerConclusion;
            _EngineerComment = engineerComment;
            _ExcludeFromReports = excludeFromReports;
            NeedUpdate = false;
            _LongSetupReasonComment = longSetupReasonComment;
            _LongSetupFixComment = longSetupFixComment;
            _LongSetupEngeneerComment = longSetupEngeneerComment;
            _ExcludedOperationsTime = excludedOperationsTime;
            _IncreaseReason = increaseReason;
            _SetupReasonOverride = setupReasonOverride;
            _SetupReasonOverrideComment = setupReasonOverrideComment;
            _SetupReasonOverrideIsMasterFault = setupReasonOverrideIsMasterFault;
            _SetupReasonOverrideMasterFaultComment = setupReasonOverrideMasterFaultComment;
            _MachiningReasonOverride = machiningReasonOverride;
            _MachiningReasonOverrideComment = machiningReasonOverrideComment;
            _MachiningReasonOverrideIsMasterFault = machiningReasonOverrideIsMasterFault;
            _MachiningReasonOverrideMasterFaultComment = machiningReasonOverrideMasterFaultComment;
            _ReasonOverrideBy = reasonOverrideBy;
            _ReasonOverrideAt = reasonOverrideAt;
        }

        public Part(Part part)
        {
            _Guid = part.Guid;
            _Machine = part.Machine;
            _Shift = part.Shift;
            _ShiftDate = part.ShiftDate;
            _Operator = part.Operator;
            _PartName = part.PartName;
            _Order = part.Order;
            _Setup = part.Setup;
            _FinishedCount = part.FinishedCount;
            _DefectiveCount = part.DefectiveCount;
            _TotalCount = part.TotalCount;
            _StartSetupTime = part.StartSetupTime;
            _StartMachiningTime = part.StartMachiningTime;
            _EndMachiningTime = part.EndMachiningTime;
            _SetupTimePlan = part.SetupTimePlan;
            _SetupTimePlanForReport = part.SetupTimePlanForReport;
            _SingleProductionTimePlan = part.SingleProductionTimePlan;
            _MachiningTime = part.MachiningTime;
            _SetupDowntimes = part.SetupDowntimes;
            _MachiningDowntimes = part.MachiningDowntimes;
            _PartialSetupTime = part.PartialSetupTime;
            _CreateNcProgramTime = part.CreateNcProgramTime;
            _MaintenanceTime = part.MaintenanceTime;
            _ToolSearchingTime = part.ToolSearchingTime;
            _ToolChangingTime = part.ToolChangingTime;
            _MentoringTime = part.MentoringTime;
            _ContactingDepartmentsTime = part.ContactingDepartmentsTime;
            _FixtureMakingTime = part.FixtureMakingTime;
            _HardwareFailureTime = part.HardwareFailureTime;
            _SpecialDowntimeTime = part.SpecialDowntimeTime;
            _OperatorComment = part.OperatorComment;
            _MasterSetupComment = part.MasterSetupComment;
            _MasterMachiningComment = part.MasterMachiningComment;
            _SpecifiedDowntimesComment = part.SpecifiedDowntimesComment;
            _UnspecifiedDowntimesComment = part.UnspecifiedDowntimesComment;
            _MasterComment = part.MasterComment;
            _MasterSetupDetail = part.MasterSetupDetail;
            _MasterMachiningDetail = part.MasterMachiningDetail;
            _FixedSetupTimePlan = part.FixedSetupTimePlan;
            _FixedProductionTimePlan = part.FixedProductionTimePlan;
            _EngineerConclusion = part.EngineerConclusion;
            _EngineerComment = part.EngineerComment;
            NeedUpdate = false;
            _LongSetupReasonComment = part.LongSetupReasonComment;
            _LongSetupFixComment = part.LongSetupFixComment;
            _LongSetupEngeneerComment = part.LongSetupEngeneerComment;
            _ExcludedOperationsTime = part.ExcludedOperationsTime;
            _IncreaseReason = part.IncreaseReason;
            _SetupReasonOverride = part.SetupReasonOverride;
            _SetupReasonOverrideComment = part.SetupReasonOverrideComment;
            _SetupReasonOverrideIsMasterFault = part.SetupReasonOverrideIsMasterFault;
            _SetupReasonOverrideMasterFaultComment = part.SetupReasonOverrideMasterFaultComment;
            _MachiningReasonOverride = part.MachiningReasonOverride;
            _MachiningReasonOverrideComment = part.MachiningReasonOverrideComment;
            _MachiningReasonOverrideIsMasterFault = part.MachiningReasonOverrideIsMasterFault;
            _MachiningReasonOverrideMasterFaultComment = part.MachiningReasonOverrideMasterFaultComment;
            _ReasonOverrideBy = part.ReasonOverrideBy;
            _ReasonOverrideAt = part.ReasonOverrideAt;
        }

        private Guid _Guid;
        /// <summary> GUID </summary>
        public Guid Guid
        {
            get => _Guid;
            set {
                if (Set(ref _Guid, value))
                {
                    NeedUpdate = true;
                    OnPropertyChanged(nameof(NeedUpdate));
                }
            }
        }

        private string _Machine;
        /// <summary> Станок </summary>
        public string Machine
        {
            get => _Machine;
            set
            {
                if (Set(ref _Machine, value))
                {
                    NeedUpdate = true;
                    OnPropertyChanged(nameof(NeedUpdate));
                }
            }
        }

        private DateTime _ShiftDate;
        /// <summary> Дата смены </summary>
        public DateTime ShiftDate
        {
            get => _ShiftDate;
            set
            {
                if (Set(ref _ShiftDate, value))
                {
                    NeedUpdate = true;
                    OnPropertyChanged(nameof(NeedUpdate));
                }
            }
        }

        private string _Shift;
        /// <summary> Смена </summary>
        public string Shift
        {
            get => _Shift;
            set
            {
                if (Set(ref _Shift, value))
                {
                    NeedUpdate = true;
                    OnPropertyChanged(nameof(NeedUpdate));
                }
            }
        }

        private string _Operator;
        /// <summary> Оператор </summary>
        public string Operator
        {
            get => _Operator;
            set
            {
                if (Set(ref _Operator, value))
                {
                    NeedUpdate = true;
                    OnPropertyChanged(nameof(NeedUpdate));
                }
            }
        }

        private string _PartName;
        /// <summary> Название детали </summary>
        public string PartName
        {
            get => _PartName;
            set
            {
                if (Set(ref _PartName, value))
                {
                    NeedUpdate = true;
                    OnPropertyChanged(nameof(NeedUpdate));
                }
            }
        }

        private string _Order;
        /// <summary> Номер маршрутного листа </summary>
        public string Order
        {
            get => _Order;
            set {
                if (Set(ref _Order, value))
                {
                    NeedUpdate = true;
                    OnPropertyChanged(nameof(NeedUpdate));
                }
            }
        }

        private int _Setup;
        /// <summary> Номер установки </summary>
        public int Setup
        {
            get => _Setup;
            set {
                if (Set(ref _Setup, value))
                {
                    NeedUpdate = true;
                    OnPropertyChanged(nameof(NeedUpdate));
                }
            }
        }

        private double _FinishedCount;
        /// <summary> Изготовлено </summary>
        public double FinishedCount
        {
            get => _FinishedCount;
            set {
                if (Set(ref _FinishedCount, value))
                {
                    NeedUpdate = true;
                    OnPropertyChanged(nameof(NeedUpdate));
                }
            }
        }

        /// <summary> Изготовлено по факту с учетом наладок </summary>
        public double FinishedCountFact
        {
            get
            {
                return StartSetupTime != StartMachiningTime && FinishedCount != 0 ? FinishedCount - 1 : FinishedCount;
            }
        }

        private int _DefectiveCount;
        /// <summary> Количество брака </summary>
        public int DefectiveCount
        {
            get => _DefectiveCount;
            set
            {
                if (Set(ref _DefectiveCount, value))
                {
                    NeedUpdate = true;
                    OnPropertyChanged(nameof(NeedUpdate));
                }
            }
        }

        private int _TotalCount;
        /// <summary> Всего партия </summary>
        public int TotalCount
        {
            get => _TotalCount;
            set {
                if (Set(ref _TotalCount, value))
                {
                    NeedUpdate = true;
                    OnPropertyChanged(nameof(NeedUpdate));
                }
            }
        }

        /// <summary> Присутствует ли деталь в списке серийных </summary>
        public bool IsSerial
        {
            get
            {
                // делать ли вхождение имен вместо полного совпадения?
                return DomainSettings.SerialParts.Contains(PartName.NormalizedPartNameWithoutComments());
            }
        }

        /// <summary>
        /// Разрешено ли редактировать нормативы детали - true если деталь не серийная, либо если серийная и редактирование разблокировано вручную.
        /// </summary>
        public bool IsEditEnabled => !IsSerial || IsUnlocked;

        private bool _IsUnlocked;
        /// <summary> Разлокировано ли редактирование (участвует только если деталь серийная) </summary>
        public bool IsUnlocked
        {
            get => _IsUnlocked;
            set
            {
                if (Set(ref _IsUnlocked, value))
                {
                    OnPropertyChanged(nameof(IsEditEnabled));
                }
            }
        }

        private DateTime _StartSetupTime;
        /// <summary> Начало наладки </summary>
        public DateTime StartSetupTime
        {
            get => _StartSetupTime;
            set {
                if (Set(ref _StartSetupTime, FixedDate(value)))
                {
                    NeedUpdate = true;
                    OnPropertyChanged(nameof(SetupTimeFact));
                    OnPropertyChanged(nameof(ProductionTimeFact));
                    OnPropertyChanged(nameof(SetupRatio));
                    OnPropertyChanged(nameof(SetupRatioTitle));
                    OnPropertyChanged(nameof(ProductionRatio));
                    OnPropertyChanged(nameof(ProductionRatioTitle));
                    OnPropertyChanged(nameof(SingleProductionTime));
                    OnPropertyChanged(nameof(SpecifiedDowntimesRatio));
                    OnPropertyChanged(nameof(SpecifiedDowntimesComment));
                    OnPropertyChanged(nameof(MasterSetupComment));
                    OnPropertyChanged(nameof(MasterMachiningComment));
                    OnPropertyChanged(nameof(MasterComment));
                    OnPropertyChanged(nameof(MasterSetupDetail));
                    OnPropertyChanged(nameof(MasterMachiningDetail));
                    OnPropertyChanged(nameof(EffectiveSetupDetail));
                    OnPropertyChanged(nameof(EffectiveMachiningDetail));
                    OnPropertyChanged(nameof(Error));
                    OnPropertyChanged(nameof(NeedUpdate));
                }
            }
        }

        private DateTime _StartMachiningTime;
        /// <summary> Завершение наладки / начало изготовления </summary>
        public DateTime StartMachiningTime
        {
            get => _StartMachiningTime;
            set {
                if (Set(ref _StartMachiningTime, FixedDate(value)))
                {
                    NeedUpdate = true;
                    OnPropertyChanged(nameof(SetupTimeFact));
                    OnPropertyChanged(nameof(ProductionTimeFact));
                    OnPropertyChanged(nameof(SetupRatio));
                    OnPropertyChanged(nameof(SetupRatioTitle));
                    OnPropertyChanged(nameof(ProductionRatio));
                    OnPropertyChanged(nameof(ProductionRatioTitle));
                    OnPropertyChanged(nameof(SingleProductionTime));
                    OnPropertyChanged(nameof(SpecifiedDowntimesRatio));
                    OnPropertyChanged(nameof(SpecifiedDowntimesComment));
                    OnPropertyChanged(nameof(MasterSetupComment));
                    OnPropertyChanged(nameof(MasterMachiningComment));
                    OnPropertyChanged(nameof(MasterComment));
                    OnPropertyChanged(nameof(MasterSetupDetail));
                    OnPropertyChanged(nameof(MasterMachiningDetail));
                    OnPropertyChanged(nameof(EffectiveSetupDetail));
                    OnPropertyChanged(nameof(EffectiveMachiningDetail));
                    OnPropertyChanged(nameof(Error));
                    OnPropertyChanged(nameof(NeedUpdate));
                }
            }
        }

        public double SetupTimeFact => (StartMachiningTime - StartSetupTime - DateTimes.GetBreaksBetween(StartSetupTime, StartMachiningTime)).TotalMinutes - SetupDowntimes - PartialSetupTime;
        public double SetupTimeFactIncludePartial => (StartMachiningTime - StartSetupTime - DateTimes.GetBreaksBetween(StartSetupTime, StartMachiningTime)).TotalMinutes - SetupDowntimes;
        public double SetupTimeFactIncludePartialAndDowntimes => (StartMachiningTime - StartSetupTime - DateTimes.GetBreaksBetween(StartSetupTime, StartMachiningTime)).TotalMinutes;
        public double SetupTimeFactFull => (StartMachiningTime - StartSetupTime).TotalMinutes;
        public double ProductionTimeFact => (EndMachiningTime - StartMachiningTime - DateTimes.GetBreaksBetween(StartMachiningTime, EndMachiningTime)).TotalMinutes - MachiningDowntimes;

        private DateTime _EndMachiningTime;
        /// <summary> Завершение изготовления </summary>
        public DateTime EndMachiningTime
        {
            get => _EndMachiningTime;
            set {
                if (Set(ref _EndMachiningTime, FixedDate(value)))
                {
                    NeedUpdate = true;
                    //ProductionTimeFact = (EndMachiningTime - StartMachiningTime - DateTimes.GetBreaksBetween(StartMachiningTime, EndMachiningTime)).TotalMinutes;
                    OnPropertyChanged(nameof(SetupTimeFact));
                    OnPropertyChanged(nameof(ProductionTimeFact));
                    OnPropertyChanged(nameof(SetupRatio));
                    OnPropertyChanged(nameof(SetupRatioTitle));
                    OnPropertyChanged(nameof(ProductionRatio));
                    OnPropertyChanged(nameof(ProductionRatioTitle));
                    OnPropertyChanged(nameof(SingleProductionTime));
                    OnPropertyChanged(nameof(SpecifiedDowntimesRatio));
                    OnPropertyChanged(nameof(SpecifiedDowntimesComment));
                    OnPropertyChanged(nameof(MasterSetupComment));
                    OnPropertyChanged(nameof(MasterMachiningComment));
                    OnPropertyChanged(nameof(MasterComment));
                    OnPropertyChanged(nameof(MasterSetupDetail));
                    OnPropertyChanged(nameof(MasterMachiningDetail));
                    OnPropertyChanged(nameof(EffectiveSetupDetail));
                    OnPropertyChanged(nameof(EffectiveMachiningDetail));
                    OnPropertyChanged(nameof(Error));
                    OnPropertyChanged(nameof(NeedUpdate));
                }
            }
        }

        private double _SetupTimePlan;
        /// <summary> Норматив наладки </summary>
        public double SetupTimePlan
        {
            get => _SetupTimePlan;
            set
            {
                if(Set(ref _SetupTimePlan, value))
                {
                    NeedUpdate = true;
                    OnPropertyChanged(nameof(SetupTimeFact));
                    OnPropertyChanged(nameof(ProductionTimeFact));
                    OnPropertyChanged(nameof(SetupRatio));
                    OnPropertyChanged(nameof(SetupRatioTitle));
                    OnPropertyChanged(nameof(ProductionRatio));
                    OnPropertyChanged(nameof(ProductionRatioTitle));
                    OnPropertyChanged(nameof(SingleProductionTime));
                    OnPropertyChanged(nameof(SpecifiedDowntimesRatio));
                    OnPropertyChanged(nameof(SpecifiedDowntimesComment));
                    OnPropertyChanged(nameof(MasterSetupComment));
                    OnPropertyChanged(nameof(MasterMachiningComment));
                    OnPropertyChanged(nameof(MasterComment));
                    OnPropertyChanged(nameof(MasterSetupDetail));
                    OnPropertyChanged(nameof(MasterMachiningDetail));
                    OnPropertyChanged(nameof(EffectiveSetupDetail));
                    OnPropertyChanged(nameof(EffectiveMachiningDetail));
                    OnPropertyChanged(nameof(Error));
                    OnPropertyChanged(nameof(NeedUpdate));
                }
            }
        }


        private double _SetupTimePlanForReport;
        /// <summary> Норматив наладки для отчета </summary>
        public double SetupTimePlanForReport
        {
            get => _SetupTimePlanForReport;
            set {
                if (Set(ref _SetupTimePlanForReport, value))
                {
                    NeedUpdate = true;
                    OnPropertyChanged(nameof(NeedUpdate));
                }
            }
        }


        private double _SingleProductionTimePlan;
        /// <summary> Штучный норматив </summary>
        public double SingleProductionTimePlan
        {
            get => _SingleProductionTimePlan;
            set {
                if (Set(ref _SingleProductionTimePlan, value))
                {
                    NeedUpdate = true;
                    OnPropertyChanged(nameof(SetupTimeFact));
                    OnPropertyChanged(nameof(ProductionTimeFact));
                    OnPropertyChanged(nameof(SetupRatio));
                    OnPropertyChanged(nameof(SetupRatioTitle));
                    OnPropertyChanged(nameof(ProductionRatio));
                    OnPropertyChanged(nameof(ProductionRatioTitle));
                    OnPropertyChanged(nameof(SingleProductionTime));
                    OnPropertyChanged(nameof(SpecifiedDowntimesRatio));
                    OnPropertyChanged(nameof(SpecifiedDowntimesComment));
                    OnPropertyChanged(nameof(MasterSetupComment));
                    OnPropertyChanged(nameof(MasterMachiningComment));
                    OnPropertyChanged(nameof(MasterComment));
                    OnPropertyChanged(nameof(MasterSetupDetail));
                    OnPropertyChanged(nameof(MasterMachiningDetail));
                    OnPropertyChanged(nameof(EffectiveSetupDetail));
                    OnPropertyChanged(nameof(EffectiveMachiningDetail));
                    OnPropertyChanged(nameof(Error));
                    OnPropertyChanged(nameof(NeedUpdate));
                }
            }
        }

        private TimeSpan _MachiningTime;
        /// <summary> Машинное время </summary>
        public TimeSpan MachiningTime
        {
            get => _MachiningTime;
            set {
                if (Set(ref _MachiningTime, value))
                {
                    NeedUpdate = true;
                    OnPropertyChanged(nameof(NeedUpdate));
                }
            }
        }

        private double _SetupDowntimes;
        /// <summary> Время простоев в наладке </summary>
        public double SetupDowntimes
        {
            get => _SetupDowntimes;
            set {
                if (Set(ref _SetupDowntimes, value))
                {
                    NeedUpdate = true;
                    OnPropertyChanged(nameof(SetupTimeFact));
                    OnPropertyChanged(nameof(ProductionTimeFact));
                    OnPropertyChanged(nameof(SetupRatio));
                    OnPropertyChanged(nameof(SetupRatioTitle));
                    OnPropertyChanged(nameof(ProductionRatio));
                    OnPropertyChanged(nameof(ProductionRatioTitle));
                    OnPropertyChanged(nameof(SingleProductionTime));
                    OnPropertyChanged(nameof(SpecifiedDowntimesRatio));
                    OnPropertyChanged(nameof(SpecifiedDowntimesComment));
                    OnPropertyChanged(nameof(MasterSetupComment));
                    OnPropertyChanged(nameof(MasterMachiningComment));
                    OnPropertyChanged(nameof(MasterComment));
                    OnPropertyChanged(nameof(MasterSetupDetail));
                    OnPropertyChanged(nameof(MasterMachiningDetail));
                    OnPropertyChanged(nameof(EffectiveSetupDetail));
                    OnPropertyChanged(nameof(EffectiveMachiningDetail));
                    OnPropertyChanged(nameof(Error));
                    OnPropertyChanged(nameof(NeedUpdate));
                }
            }
        }

        /// <summary>
        /// Простои в наладке с добавленным превышением норматива частичной наладкой
        /// </summary>
        public double SetupDowntimesWithPartialExcess
        {
            get
            {
                double partialExcess = 0;
                if (PartialSetupTime > SetupTimePlanForReport) partialExcess = PartialSetupTime - SetupTimePlanForReport;
                return SetupDowntimes + partialExcess;
            }
        }


        private double _MachiningDowntimes;
        /// <summary> Время простоев в изготовлении </summary>
        public double MachiningDowntimes
        {
            get => _MachiningDowntimes;
            set {
                if (Set(ref _MachiningDowntimes, value))
                {
                    NeedUpdate = true;
                    OnPropertyChanged(nameof(SetupTimeFact));
                    OnPropertyChanged(nameof(ProductionTimeFact));
                    OnPropertyChanged(nameof(SetupRatio));
                    OnPropertyChanged(nameof(SetupRatioTitle));
                    OnPropertyChanged(nameof(ProductionRatio));
                    OnPropertyChanged(nameof(ProductionRatioTitle));
                    OnPropertyChanged(nameof(SingleProductionTime));
                    OnPropertyChanged(nameof(SpecifiedDowntimesRatio));
                    OnPropertyChanged(nameof(SpecifiedDowntimesComment));
                    OnPropertyChanged(nameof(MasterSetupComment));
                    OnPropertyChanged(nameof(MasterMachiningComment));
                    OnPropertyChanged(nameof(MasterComment));
                    OnPropertyChanged(nameof(MasterSetupDetail));
                    OnPropertyChanged(nameof(MasterMachiningDetail));
                    OnPropertyChanged(nameof(EffectiveSetupDetail));
                    OnPropertyChanged(nameof(EffectiveMachiningDetail));
                    OnPropertyChanged(nameof(Error));
                    OnPropertyChanged(nameof(NeedUpdate));
                }
            }
        }


        private double _PartialSetupTime;
        /// <summary> Время простоя "Частичная наладка" </summary>
        public double PartialSetupTime
        {
            get => _PartialSetupTime;
            set {
                if (Set(ref _PartialSetupTime, value))
                {
                    NeedUpdate = true;
                    OnPropertyChanged(nameof(SetupTimeFact));
                    OnPropertyChanged(nameof(ProductionTimeFact));
                    OnPropertyChanged(nameof(SetupRatio));
                    OnPropertyChanged(nameof(SetupRatioTitle));
                    OnPropertyChanged(nameof(ProductionRatio));
                    OnPropertyChanged(nameof(ProductionRatioTitle));
                    OnPropertyChanged(nameof(SingleProductionTime));
                    OnPropertyChanged(nameof(SpecifiedDowntimesRatio));
                    OnPropertyChanged(nameof(SpecifiedDowntimesComment));
                    OnPropertyChanged(nameof(MasterSetupComment));
                    OnPropertyChanged(nameof(MasterMachiningComment));
                    OnPropertyChanged(nameof(MasterComment));
                    OnPropertyChanged(nameof(MasterSetupDetail));
                    OnPropertyChanged(nameof(MasterMachiningDetail));
                    OnPropertyChanged(nameof(EffectiveSetupDetail));
                    OnPropertyChanged(nameof(EffectiveMachiningDetail));
                    OnPropertyChanged(nameof(Error));
                    OnPropertyChanged(nameof(NeedUpdate));
                }
            }
        }


        private double _CreateNcProgramTime;
        /// <summary> Время простоя "Написание УП" </summary>
        public double CreateNcProgramTime
        {
            get => _CreateNcProgramTime;
            set
            {
                if (Set(ref _CreateNcProgramTime, value))
                {
                    NeedUpdate = true;
                    OnPropertyChanged(nameof(SetupTimeFact));
                    OnPropertyChanged(nameof(ProductionTimeFact));
                    OnPropertyChanged(nameof(SetupRatio));
                    OnPropertyChanged(nameof(SetupRatioTitle));
                    OnPropertyChanged(nameof(ProductionRatio));
                    OnPropertyChanged(nameof(ProductionRatioTitle));
                    OnPropertyChanged(nameof(SingleProductionTime));
                    OnPropertyChanged(nameof(SpecifiedDowntimesRatio));
                    OnPropertyChanged(nameof(SpecifiedDowntimesComment));
                    OnPropertyChanged(nameof(MasterSetupComment));
                    OnPropertyChanged(nameof(MasterMachiningComment));
                    OnPropertyChanged(nameof(MasterComment));
                    OnPropertyChanged(nameof(MasterSetupDetail));
                    OnPropertyChanged(nameof(MasterMachiningDetail));
                    OnPropertyChanged(nameof(EffectiveSetupDetail));
                    OnPropertyChanged(nameof(EffectiveMachiningDetail));
                    OnPropertyChanged(nameof(Error));
                    OnPropertyChanged(nameof(NeedUpdate));
                }
            }
        }

        private double _MaintenanceTime;
        /// <summary> Время простоя "Обслуживание" </summary>
        public double MaintenanceTime
        {
            get => _MaintenanceTime;
            set {
                if (Set(ref _MaintenanceTime, value))
                {
                    NeedUpdate = true;
                    OnPropertyChanged(nameof(SetupTimeFact));
                    OnPropertyChanged(nameof(ProductionTimeFact));
                    OnPropertyChanged(nameof(SetupRatio));
                    OnPropertyChanged(nameof(SetupRatioTitle));
                    OnPropertyChanged(nameof(ProductionRatio));
                    OnPropertyChanged(nameof(ProductionRatioTitle));
                    OnPropertyChanged(nameof(SingleProductionTime));
                    OnPropertyChanged(nameof(SpecifiedDowntimesRatio));
                    OnPropertyChanged(nameof(SpecifiedDowntimesComment));
                    OnPropertyChanged(nameof(MasterSetupComment));
                    OnPropertyChanged(nameof(MasterMachiningComment));
                    OnPropertyChanged(nameof(MasterComment));
                    OnPropertyChanged(nameof(MasterSetupDetail));
                    OnPropertyChanged(nameof(MasterMachiningDetail));
                    OnPropertyChanged(nameof(EffectiveSetupDetail));
                    OnPropertyChanged(nameof(EffectiveMachiningDetail));
                    OnPropertyChanged(nameof(Error));
                    OnPropertyChanged(nameof(NeedUpdate));
                }
            }
        }


        private double _ToolSearchingTime;
        /// <summary> Время простоя "Поиск и получение инструмента" </summary>
        public double ToolSearchingTime
        {
            get => _ToolSearchingTime;
            set {
                if (Set(ref _ToolSearchingTime, value))
                {
                    NeedUpdate = true;
                    OnPropertyChanged(nameof(SetupTimeFact));
                    OnPropertyChanged(nameof(ProductionTimeFact));
                    OnPropertyChanged(nameof(SetupRatio));
                    OnPropertyChanged(nameof(SetupRatioTitle));
                    OnPropertyChanged(nameof(ProductionRatio));
                    OnPropertyChanged(nameof(ProductionRatioTitle));
                    OnPropertyChanged(nameof(SingleProductionTime));
                    OnPropertyChanged(nameof(SpecifiedDowntimesRatio));
                    OnPropertyChanged(nameof(SpecifiedDowntimesComment));
                    OnPropertyChanged(nameof(MasterSetupComment));
                    OnPropertyChanged(nameof(MasterMachiningComment));
                    OnPropertyChanged(nameof(MasterComment));
                    OnPropertyChanged(nameof(MasterSetupDetail));
                    OnPropertyChanged(nameof(MasterMachiningDetail));
                    OnPropertyChanged(nameof(EffectiveSetupDetail));
                    OnPropertyChanged(nameof(EffectiveMachiningDetail));
                    OnPropertyChanged(nameof(Error));
                    OnPropertyChanged(nameof(NeedUpdate));
                }
            }
        }

        private double _ToolChangingTime;
        /// <summary> Время простоя "Замена инструмента" </summary>
        public double ToolChangingTime
        {
            get => _ToolChangingTime;
            set
            {
                if (Set(ref _ToolChangingTime, value))
                {
                    NeedUpdate = true;
                    OnPropertyChanged(nameof(SetupTimeFact));
                    OnPropertyChanged(nameof(ProductionTimeFact));
                    OnPropertyChanged(nameof(SetupRatio));
                    OnPropertyChanged(nameof(SetupRatioTitle));
                    OnPropertyChanged(nameof(ProductionRatio));
                    OnPropertyChanged(nameof(ProductionRatioTitle));
                    OnPropertyChanged(nameof(SingleProductionTime));
                    OnPropertyChanged(nameof(SpecifiedDowntimesRatio));
                    OnPropertyChanged(nameof(SpecifiedDowntimesComment));
                    OnPropertyChanged(nameof(MasterSetupComment));
                    OnPropertyChanged(nameof(MasterMachiningComment));
                    OnPropertyChanged(nameof(MasterComment));
                    OnPropertyChanged(nameof(MasterSetupDetail));
                    OnPropertyChanged(nameof(MasterMachiningDetail));
                    OnPropertyChanged(nameof(EffectiveSetupDetail));
                    OnPropertyChanged(nameof(EffectiveMachiningDetail));
                    OnPropertyChanged(nameof(Error));
                    OnPropertyChanged(nameof(NeedUpdate));
                }
            }
        }


        private double _MentoringTime;
        /// <summary> Время простоя "Помощь / обучение" </summary>
        public double MentoringTime
        {
            get => _MentoringTime;
            set {
                if (Set(ref _MentoringTime, value))
                {
                    NeedUpdate = true;
                    OnPropertyChanged(nameof(SetupTimeFact));
                    OnPropertyChanged(nameof(ProductionTimeFact));
                    OnPropertyChanged(nameof(SetupRatio));
                    OnPropertyChanged(nameof(SetupRatioTitle));
                    OnPropertyChanged(nameof(ProductionRatio));
                    OnPropertyChanged(nameof(ProductionRatioTitle));
                    OnPropertyChanged(nameof(SingleProductionTime));
                    OnPropertyChanged(nameof(SpecifiedDowntimesRatio));
                    OnPropertyChanged(nameof(SpecifiedDowntimesComment));
                    OnPropertyChanged(nameof(MasterSetupComment));
                    OnPropertyChanged(nameof(MasterMachiningComment));
                    OnPropertyChanged(nameof(MasterComment));
                    OnPropertyChanged(nameof(MasterSetupDetail));
                    OnPropertyChanged(nameof(MasterMachiningDetail));
                    OnPropertyChanged(nameof(EffectiveSetupDetail));
                    OnPropertyChanged(nameof(EffectiveMachiningDetail));
                    OnPropertyChanged(nameof(Error));
                    OnPropertyChanged(nameof(NeedUpdate));
                }
            }
        }


        private double _ContactingDepartmentsTime;
        /// <summary> Время простоя "Обращение в другие службы" </summary>
        public double ContactingDepartmentsTime
        {
            get => _ContactingDepartmentsTime;
            set {
                if (Set(ref _ContactingDepartmentsTime, value))
                {
                    NeedUpdate = true;
                    OnPropertyChanged(nameof(SetupTimeFact));
                    OnPropertyChanged(nameof(ProductionTimeFact));
                    OnPropertyChanged(nameof(SetupRatio));
                    OnPropertyChanged(nameof(SetupRatioTitle));
                    OnPropertyChanged(nameof(ProductionRatio));
                    OnPropertyChanged(nameof(ProductionRatioTitle));
                    OnPropertyChanged(nameof(SingleProductionTime));
                    OnPropertyChanged(nameof(SpecifiedDowntimesRatio));
                    OnPropertyChanged(nameof(SpecifiedDowntimesComment));
                    OnPropertyChanged(nameof(MasterSetupComment));
                    OnPropertyChanged(nameof(MasterMachiningComment));
                    OnPropertyChanged(nameof(MasterComment));
                    OnPropertyChanged(nameof(MasterSetupDetail));
                    OnPropertyChanged(nameof(MasterMachiningDetail));
                    OnPropertyChanged(nameof(EffectiveSetupDetail));
                    OnPropertyChanged(nameof(EffectiveMachiningDetail));
                    OnPropertyChanged(nameof(Error));
                    OnPropertyChanged(nameof(NeedUpdate));
                }
            }
        }


        private double _FixtureMakingTime;
        /// <summary> Время простоя "Изготовление оснастки и калибров" </summary>
        public double FixtureMakingTime
        {
            get => _FixtureMakingTime;
            set {
                if (Set(ref _FixtureMakingTime, value))
                {
                    NeedUpdate = true;
                    OnPropertyChanged(nameof(SetupTimeFact));
                    OnPropertyChanged(nameof(ProductionTimeFact));
                    OnPropertyChanged(nameof(SetupRatio));
                    OnPropertyChanged(nameof(SetupRatioTitle));
                    OnPropertyChanged(nameof(ProductionRatio));
                    OnPropertyChanged(nameof(ProductionRatioTitle));
                    OnPropertyChanged(nameof(SingleProductionTime));
                    OnPropertyChanged(nameof(SpecifiedDowntimesRatio));
                    OnPropertyChanged(nameof(SpecifiedDowntimesComment));
                    OnPropertyChanged(nameof(MasterSetupComment));
                    OnPropertyChanged(nameof(MasterMachiningComment));
                    OnPropertyChanged(nameof(MasterComment));
                    OnPropertyChanged(nameof(MasterSetupDetail));
                    OnPropertyChanged(nameof(MasterMachiningDetail));
                    OnPropertyChanged(nameof(EffectiveSetupDetail));
                    OnPropertyChanged(nameof(EffectiveMachiningDetail));
                    OnPropertyChanged(nameof(Error));
                    OnPropertyChanged(nameof(NeedUpdate));
                }
            }
        }


        private double _HardwareFailureTime;
        /// <summary> Время простоя "Отказ оборудования" </summary>
        public double HardwareFailureTime
        {
            get => _HardwareFailureTime;
            set {
                if (Set(ref _HardwareFailureTime, value))
                {
                    NeedUpdate = true;
                    OnPropertyChanged(nameof(SetupTimeFact));
                    OnPropertyChanged(nameof(ProductionTimeFact));
                    OnPropertyChanged(nameof(SetupRatio));
                    OnPropertyChanged(nameof(SetupRatioTitle));
                    OnPropertyChanged(nameof(ProductionRatio));
                    OnPropertyChanged(nameof(ProductionRatioTitle));
                    OnPropertyChanged(nameof(SingleProductionTime));
                    OnPropertyChanged(nameof(SpecifiedDowntimesRatio));
                    OnPropertyChanged(nameof(SpecifiedDowntimesComment));
                    OnPropertyChanged(nameof(MasterSetupComment));
                    OnPropertyChanged(nameof(MasterMachiningComment));
                    OnPropertyChanged(nameof(MasterComment));
                    OnPropertyChanged(nameof(MasterSetupDetail));
                    OnPropertyChanged(nameof(MasterMachiningDetail));
                    OnPropertyChanged(nameof(EffectiveSetupDetail));
                    OnPropertyChanged(nameof(EffectiveMachiningDetail));
                    OnPropertyChanged(nameof(Error));
                    OnPropertyChanged(nameof(NeedUpdate));
                }
            }
        }

        private double _SpecialDowntimeTime;
        /// <summary> Время простев специально исключённых из расчётов </summary>
        public double SpecialDowntimeTime
        {
            get => _SpecialDowntimeTime;
            set
            {
                if (Set(ref _SpecialDowntimeTime, value))
                {
                    NeedUpdate = true;
                    OnPropertyChanged(nameof(SetupTimeFact));
                    OnPropertyChanged(nameof(ProductionTimeFact));
                    OnPropertyChanged(nameof(SetupRatio));
                    OnPropertyChanged(nameof(SetupRatioTitle));
                    OnPropertyChanged(nameof(ProductionRatio));
                    OnPropertyChanged(nameof(ProductionRatioTitle));
                    OnPropertyChanged(nameof(SingleProductionTime));
                    OnPropertyChanged(nameof(SpecifiedDowntimesRatio));
                    OnPropertyChanged(nameof(SpecifiedDowntimesComment));
                    OnPropertyChanged(nameof(MasterSetupComment));
                    OnPropertyChanged(nameof(MasterMachiningComment));
                    OnPropertyChanged(nameof(MasterComment));
                    OnPropertyChanged(nameof(MasterSetupDetail));
                    OnPropertyChanged(nameof(MasterMachiningDetail));
                    OnPropertyChanged(nameof(EffectiveSetupDetail));
                    OnPropertyChanged(nameof(EffectiveMachiningDetail));
                    OnPropertyChanged(nameof(Error));
                    OnPropertyChanged(nameof(NeedUpdate));
                }
            }
        }


        private string _OperatorComment;
        /// <summary> Комментарий оператора </summary>
        public string OperatorComment
        {
            get => _OperatorComment;
            set {
                if (Set(ref _OperatorComment, value))
                {
                    NeedUpdate = true;
                    OnPropertyChanged(nameof(NeedUpdate));
                }
            }
        }


        private string _MasterSetupComment;
        /// <summary> Комментарий к отклонениям от нормативов в наладке </summary>
        public string MasterSetupComment
        {
            get => _MasterSetupComment;
            set {
                if (Set(ref _MasterSetupComment, value))
                {
                    NeedUpdate = true;
                    OnPropertyChanged(nameof(EffectiveSetupReason));
                    OnPropertyChanged(nameof(SetupOverrideTooltip));
                    OnPropertyChanged(nameof(SetupTimeFact));
                    OnPropertyChanged(nameof(ProductionTimeFact));
                    OnPropertyChanged(nameof(SetupRatio));
                    OnPropertyChanged(nameof(SetupRatioTitle));
                    OnPropertyChanged(nameof(ProductionRatio));
                    OnPropertyChanged(nameof(ProductionRatioTitle));
                    OnPropertyChanged(nameof(SingleProductionTime));
                    OnPropertyChanged(nameof(SpecifiedDowntimesRatio));
                    OnPropertyChanged(nameof(SpecifiedDowntimesComment));
                    OnPropertyChanged(nameof(MasterSetupComment));
                    OnPropertyChanged(nameof(MasterMachiningComment));
                    OnPropertyChanged(nameof(MasterComment));
                    OnPropertyChanged(nameof(MasterSetupDetail));
                    OnPropertyChanged(nameof(MasterMachiningDetail));
                    OnPropertyChanged(nameof(EffectiveSetupDetail));
                    OnPropertyChanged(nameof(EffectiveMachiningDetail));
                    OnPropertyChanged(nameof(Error));
                    OnPropertyChanged(nameof(NeedUpdate));
                }
            }
        }


        private string _MasterMachiningComment;
        /// <summary> Комментарий к отклонениям от нормативов в изготовлении </summary>
        public string MasterMachiningComment
        {
            get => _MasterMachiningComment;
            set {
                if (Set(ref _MasterMachiningComment, value))
                {
                    NeedUpdate = true;
                    OnPropertyChanged(nameof(EffectiveMachiningReason));
                    OnPropertyChanged(nameof(MachiningOverrideTooltip));
                    OnPropertyChanged(nameof(SetupTimeFact));
                    OnPropertyChanged(nameof(ProductionTimeFact));
                    OnPropertyChanged(nameof(SetupRatio));
                    OnPropertyChanged(nameof(SetupRatioTitle));
                    OnPropertyChanged(nameof(ProductionRatio));
                    OnPropertyChanged(nameof(ProductionRatioTitle));
                    OnPropertyChanged(nameof(SingleProductionTime));
                    OnPropertyChanged(nameof(SpecifiedDowntimesRatio));
                    OnPropertyChanged(nameof(SpecifiedDowntimesComment));
                    OnPropertyChanged(nameof(MasterSetupComment));
                    OnPropertyChanged(nameof(MasterMachiningComment));
                    OnPropertyChanged(nameof(MasterComment));
                    OnPropertyChanged(nameof(MasterSetupDetail));
                    OnPropertyChanged(nameof(MasterMachiningDetail));
                    OnPropertyChanged(nameof(EffectiveSetupDetail));
                    OnPropertyChanged(nameof(EffectiveMachiningDetail));
                    OnPropertyChanged(nameof(Error));
                    OnPropertyChanged(nameof(NeedUpdate));
                }
            }
        }


        private string _SpecifiedDowntimesComment;
        /// <summary> Комментарий к зарегистрированным простоям </summary>
        public string SpecifiedDowntimesComment
        {
            get => _SpecifiedDowntimesComment;
            set {
                if (Set(ref _SpecifiedDowntimesComment, value))
                {
                    NeedUpdate = true;
                    OnPropertyChanged(nameof(SetupTimeFact));
                    OnPropertyChanged(nameof(ProductionTimeFact));
                    OnPropertyChanged(nameof(SetupRatio));
                    OnPropertyChanged(nameof(SetupRatioTitle));
                    OnPropertyChanged(nameof(ProductionRatio));
                    OnPropertyChanged(nameof(ProductionRatioTitle));
                    OnPropertyChanged(nameof(SingleProductionTime));
                    OnPropertyChanged(nameof(SpecifiedDowntimesRatio));
                    OnPropertyChanged(nameof(SpecifiedDowntimesComment));
                    OnPropertyChanged(nameof(MasterSetupComment));
                    OnPropertyChanged(nameof(MasterMachiningComment));
                    OnPropertyChanged(nameof(MasterComment));
                    OnPropertyChanged(nameof(MasterSetupDetail));
                    OnPropertyChanged(nameof(MasterMachiningDetail));
                    OnPropertyChanged(nameof(EffectiveSetupDetail));
                    OnPropertyChanged(nameof(EffectiveMachiningDetail));
                    OnPropertyChanged(nameof(Error));
                    OnPropertyChanged(nameof(NeedUpdate));
                }
            }
        }


        private string _UnspecifiedDowntimesComment;
        /// <summary> Комментарий к незарегистрированным простоям </summary>
        public string UnspecifiedDowntimesComment
        {
            get => _UnspecifiedDowntimesComment;
            set {
                if (Set(ref _UnspecifiedDowntimesComment, value))
                {
                    NeedUpdate = true;
                    OnPropertyChanged(nameof(SetupTimeFact));
                    OnPropertyChanged(nameof(ProductionTimeFact));
                    OnPropertyChanged(nameof(SetupRatio));
                    OnPropertyChanged(nameof(SetupRatioTitle));
                    OnPropertyChanged(nameof(ProductionRatio));
                    OnPropertyChanged(nameof(ProductionRatioTitle));
                    OnPropertyChanged(nameof(SingleProductionTime));
                    OnPropertyChanged(nameof(SpecifiedDowntimesRatio));
                    OnPropertyChanged(nameof(SpecifiedDowntimesComment));
                    OnPropertyChanged(nameof(MasterSetupComment));
                    OnPropertyChanged(nameof(MasterMachiningComment));
                    OnPropertyChanged(nameof(MasterComment));
                    OnPropertyChanged(nameof(MasterSetupDetail));
                    OnPropertyChanged(nameof(MasterMachiningDetail));
                    OnPropertyChanged(nameof(EffectiveSetupDetail));
                    OnPropertyChanged(nameof(EffectiveMachiningDetail));
                    OnPropertyChanged(nameof(Error));
                    OnPropertyChanged(nameof(NeedUpdate));
                }
            }
        }


        private string _MasterComment;
        /// <summary> Комментарий мастера (архив, до разделения на наладку/изготовление) </summary>
        public string MasterComment
        {
            get => _MasterComment;
            set {
                if (Set(ref _MasterComment, value))
                {
                    NeedUpdate = true;
                    OnPropertyChanged(nameof(SetupTimeFact));
                    OnPropertyChanged(nameof(ProductionTimeFact));
                    OnPropertyChanged(nameof(SetupRatio));
                    OnPropertyChanged(nameof(SetupRatioTitle));
                    OnPropertyChanged(nameof(ProductionRatio));
                    OnPropertyChanged(nameof(ProductionRatioTitle));
                    OnPropertyChanged(nameof(SingleProductionTime));
                    OnPropertyChanged(nameof(SpecifiedDowntimesRatio));
                    OnPropertyChanged(nameof(SpecifiedDowntimesComment));
                    OnPropertyChanged(nameof(MasterSetupComment));
                    OnPropertyChanged(nameof(MasterMachiningComment));
                    OnPropertyChanged(nameof(MasterComment));
                    OnPropertyChanged(nameof(MasterSetupDetail));
                    OnPropertyChanged(nameof(MasterMachiningDetail));
                    OnPropertyChanged(nameof(EffectiveSetupDetail));
                    OnPropertyChanged(nameof(EffectiveMachiningDetail));
                    OnPropertyChanged(nameof(Error));
                    OnPropertyChanged(nameof(NeedUpdate));
                }
            }
        }


        private string _MasterSetupDetail;
        /// <summary> Комментарий мастера к отклонениям в наладке </summary>
        public string MasterSetupDetail
        {
            get => _MasterSetupDetail;
            set {
                if (Set(ref _MasterSetupDetail, value))
                {
                    NeedUpdate = true;
                    OnPropertyChanged(nameof(SetupTimeFact));
                    OnPropertyChanged(nameof(ProductionTimeFact));
                    OnPropertyChanged(nameof(SetupRatio));
                    OnPropertyChanged(nameof(SetupRatioTitle));
                    OnPropertyChanged(nameof(ProductionRatio));
                    OnPropertyChanged(nameof(ProductionRatioTitle));
                    OnPropertyChanged(nameof(SingleProductionTime));
                    OnPropertyChanged(nameof(SpecifiedDowntimesRatio));
                    OnPropertyChanged(nameof(SpecifiedDowntimesComment));
                    OnPropertyChanged(nameof(MasterSetupComment));
                    OnPropertyChanged(nameof(MasterMachiningComment));
                    OnPropertyChanged(nameof(MasterComment));
                    OnPropertyChanged(nameof(MasterSetupDetail));
                    OnPropertyChanged(nameof(MasterMachiningDetail));
                    OnPropertyChanged(nameof(EffectiveSetupDetail));
                    OnPropertyChanged(nameof(EffectiveMachiningDetail));
                    OnPropertyChanged(nameof(Error));
                    OnPropertyChanged(nameof(NeedUpdate));
                }
            }
        }


        private string _MasterMachiningDetail;
        /// <summary> Комментарий мастера к отклонениям в изготовлении </summary>
        public string MasterMachiningDetail
        {
            get => _MasterMachiningDetail;
            set {
                if (Set(ref _MasterMachiningDetail, value))
                {
                    NeedUpdate = true;
                    OnPropertyChanged(nameof(SetupTimeFact));
                    OnPropertyChanged(nameof(ProductionTimeFact));
                    OnPropertyChanged(nameof(SetupRatio));
                    OnPropertyChanged(nameof(SetupRatioTitle));
                    OnPropertyChanged(nameof(ProductionRatio));
                    OnPropertyChanged(nameof(ProductionRatioTitle));
                    OnPropertyChanged(nameof(SingleProductionTime));
                    OnPropertyChanged(nameof(SpecifiedDowntimesRatio));
                    OnPropertyChanged(nameof(SpecifiedDowntimesComment));
                    OnPropertyChanged(nameof(MasterSetupComment));
                    OnPropertyChanged(nameof(MasterMachiningComment));
                    OnPropertyChanged(nameof(MasterComment));
                    OnPropertyChanged(nameof(MasterSetupDetail));
                    OnPropertyChanged(nameof(MasterMachiningDetail));
                    OnPropertyChanged(nameof(EffectiveSetupDetail));
                    OnPropertyChanged(nameof(EffectiveMachiningDetail));
                    OnPropertyChanged(nameof(Error));
                    OnPropertyChanged(nameof(NeedUpdate));
                }
            }
        }


        private double _FixedSetupTimePlan;
        /// <summary> Исправленный норматив наладки </summary>
        public double FixedSetupTimePlan
        {
            get => _FixedSetupTimePlan;
            set
            {
                NeedUpdate = true;
                Set(ref _FixedSetupTimePlan, value);
            }
        }


        private double _FixedProductionTimePlan;
        /// <summary> Исправленный норматив на изготовление </summary>
        public double FixedProductionTimePlan
        {
            get => _FixedProductionTimePlan;
            set
            {
                NeedUpdate = true;
                Set(ref _FixedProductionTimePlan, value);
            }
        }


        private static bool _CalcFixed;
        /// <summary> Описание </summary>
        public static bool CalcFixed
        {
            get => _CalcFixed;
            set 
            {
                _CalcFixed = value;
            }
        }


        public double SetupTimePlanForCalc => FixedSetupTimePlan > 0 && CalcFixed ? FixedSetupTimePlan : SetupTimePlan;

        public double ProductionTimePlanForCalc => FixedProductionTimePlan > 0 && CalcFixed ? FixedProductionTimePlan : SingleProductionTimePlan;

        private string _EngineerConclusion;
        /// <summary> Заключение техотдела </summary>
        public string EngineerConclusion
        {
            get => _EngineerConclusion;
            set {
                if (Set(ref _EngineerConclusion, value))
                {
                    NeedUpdate = true;
                    OnPropertyChanged(nameof(NeedUpdate));
                }
            }
        }

        private string _EngineerComment;
        /// <summary> Комментарий техотдела </summary>
        public string EngineerComment
        {
            get => _EngineerComment;
            set {
                if (Set(ref _EngineerComment, value))
                {
                    NeedUpdate = true;
                    OnPropertyChanged(nameof(NeedUpdate));
                }
            }
        }

        #region Переопределение причин аналитиком (СГТ 1)
        // Поля мастера после заполнения мастером не правятся — решение аналитика живёт
        // здесь, параллельным слоем. Это сохраняет отметку мастера как его официальную
        // позицию (регламент, п. 4.3) и как метрику качества заполнения, а историю типовых
        // причин — пригодной для статистики: видно и что выбрал мастер, и на что исправили.
        // В гриде колонка остаётся одна и показывает Effective*Reason.

        private string _SetupReasonOverride;
        /// <summary> Причина отклонения наладки, назначенная аналитиком вместо выбора мастера </summary>
        public string SetupReasonOverride
        {
            get => _SetupReasonOverride;
            set {
                if (Set(ref _SetupReasonOverride, value))
                {
                    NeedUpdate = true;
                    OnPropertyChanged(nameof(EffectiveSetupReason));
                    OnPropertyChanged(nameof(EffectiveSetupDetail));
                    OnPropertyChanged(nameof(HasSetupReasonOverride));
                    OnPropertyChanged(nameof(SetupOverrideTooltip));
                    OnPropertyChanged(nameof(NeedUpdate));
                }
            }
        }

        private string _SetupReasonOverrideComment;
        /// <summary> Обоснование аналитика к переопределению причины наладки </summary>
        public string SetupReasonOverrideComment
        {
            get => _SetupReasonOverrideComment;
            set {
                if (Set(ref _SetupReasonOverrideComment, value))
                {
                    NeedUpdate = true;
                    OnPropertyChanged(nameof(EffectiveSetupDetail));
                    OnPropertyChanged(nameof(SetupOverrideTooltip));
                    OnPropertyChanged(nameof(NeedUpdate));
                }
            }
        }

        private bool _SetupReasonOverrideIsMasterFault;
        /// <summary>
        /// Считать ли переопределение причины наладки ошибкой мастера. По умолчанию false —
        /// сам факт переопределения ещё не значит, что мастер виноват. Аналитик ставит флаг,
        /// когда действительно была возможность выбрать правильно (СГТ смотрит историю
        /// изготовления, Winnum, 1С — мастер видит только смену и цифры).
        /// </summary>
        public bool SetupReasonOverrideIsMasterFault
        {
            get => _SetupReasonOverrideIsMasterFault;
            set {
                if (Set(ref _SetupReasonOverrideIsMasterFault, value))
                {
                    NeedUpdate = true;
                    OnPropertyChanged(nameof(SetupOverrideTooltip));
                    OnPropertyChanged(nameof(NeedUpdate));
                }
            }
        }

        private string _SetupReasonOverrideMasterFaultComment;
        /// <summary> Пояснение аналитика, в чём именно ошибся мастер (наладка). Заполняется только при <see cref="SetupReasonOverrideIsMasterFault"/> = true. </summary>
        public string SetupReasonOverrideMasterFaultComment
        {
            get => _SetupReasonOverrideMasterFaultComment;
            set {
                if (Set(ref _SetupReasonOverrideMasterFaultComment, value))
                {
                    NeedUpdate = true;
                    OnPropertyChanged(nameof(SetupOverrideTooltip));
                    OnPropertyChanged(nameof(NeedUpdate));
                }
            }
        }

        private string _MachiningReasonOverride;
        /// <summary> Причина отклонения изготовления, назначенная аналитиком вместо выбора мастера </summary>
        public string MachiningReasonOverride
        {
            get => _MachiningReasonOverride;
            set {
                if (Set(ref _MachiningReasonOverride, value))
                {
                    NeedUpdate = true;
                    OnPropertyChanged(nameof(EffectiveMachiningReason));
                    OnPropertyChanged(nameof(EffectiveMachiningDetail));
                    OnPropertyChanged(nameof(HasMachiningReasonOverride));
                    OnPropertyChanged(nameof(MachiningOverrideTooltip));
                    OnPropertyChanged(nameof(NeedUpdate));
                }
            }
        }

        private string _MachiningReasonOverrideComment;
        /// <summary> Обоснование аналитика к переопределению причины изготовления </summary>
        public string MachiningReasonOverrideComment
        {
            get => _MachiningReasonOverrideComment;
            set {
                if (Set(ref _MachiningReasonOverrideComment, value))
                {
                    NeedUpdate = true;
                    OnPropertyChanged(nameof(EffectiveMachiningDetail));
                    OnPropertyChanged(nameof(MachiningOverrideTooltip));
                    OnPropertyChanged(nameof(NeedUpdate));
                }
            }
        }

        private bool _MachiningReasonOverrideIsMasterFault;
        /// <summary> Считать ли переопределение причины изготовления ошибкой мастера. См. <see cref="SetupReasonOverrideIsMasterFault"/>. </summary>
        public bool MachiningReasonOverrideIsMasterFault
        {
            get => _MachiningReasonOverrideIsMasterFault;
            set {
                if (Set(ref _MachiningReasonOverrideIsMasterFault, value))
                {
                    NeedUpdate = true;
                    OnPropertyChanged(nameof(MachiningOverrideTooltip));
                    OnPropertyChanged(nameof(NeedUpdate));
                }
            }
        }

        private string _MachiningReasonOverrideMasterFaultComment;
        /// <summary> Пояснение аналитика, в чём именно ошибся мастер (изготовление). См. <see cref="SetupReasonOverrideMasterFaultComment"/>. </summary>
        public string MachiningReasonOverrideMasterFaultComment
        {
            get => _MachiningReasonOverrideMasterFaultComment;
            set {
                if (Set(ref _MachiningReasonOverrideMasterFaultComment, value))
                {
                    NeedUpdate = true;
                    OnPropertyChanged(nameof(MachiningOverrideTooltip));
                    OnPropertyChanged(nameof(NeedUpdate));
                }
            }
        }

        private string _ReasonOverrideBy;
        /// <summary> Кто переопределил причины. Общее на запись: обе категории правятся в одну сессию разбора. </summary>
        public string ReasonOverrideBy
        {
            get => _ReasonOverrideBy;
            set {
                if (Set(ref _ReasonOverrideBy, value))
                {
                    NeedUpdate = true;
                    OnPropertyChanged(nameof(SetupOverrideTooltip));
                    OnPropertyChanged(nameof(MachiningOverrideTooltip));
                    OnPropertyChanged(nameof(NeedUpdate));
                }
            }
        }

        private DateTime? _ReasonOverrideAt;
        /// <summary> Когда переопределены причины. Общее на запись, см. <see cref="ReasonOverrideBy"/>. </summary>
        public DateTime? ReasonOverrideAt
        {
            get => _ReasonOverrideAt;
            set {
                if (Set(ref _ReasonOverrideAt, value))
                {
                    NeedUpdate = true;
                    OnPropertyChanged(nameof(SetupOverrideTooltip));
                    OnPropertyChanged(nameof(MachiningOverrideTooltip));
                    OnPropertyChanged(nameof(NeedUpdate));
                }
            }
        }

        public bool HasSetupReasonOverride => !string.IsNullOrWhiteSpace(SetupReasonOverride);

        public bool HasMachiningReasonOverride => !string.IsNullOrWhiteSpace(MachiningReasonOverride);

        /// <summary> Итоговая причина отклонения наладки: решение аналитика, если есть, иначе отметка мастера. </summary>
        public string EffectiveSetupReason =>
            HasSetupReasonOverride ? SetupReasonOverride : MasterSetupComment;

        /// <summary> Итоговая причина отклонения изготовления: решение аналитика, если есть, иначе отметка мастера. </summary>
        public string EffectiveMachiningReason =>
            HasMachiningReasonOverride ? MachiningReasonOverride : MasterMachiningComment;

        /// <summary>
        /// Итоговая детализация отклонения наладки: обоснование аналитика к переопределению,
        /// если оно есть, иначе комментарий мастера. Комментарий мастера обычно поясняет именно
        /// его исходную причину — при переопределении он перестаёт быть релевантным, поэтому
        /// заменяется обоснованием СГТ, а не показывается рядом с чужой причиной.
        /// </summary>
        public string EffectiveSetupDetail =>
            HasSetupReasonOverride ? SetupReasonOverrideComment : MasterSetupDetail;

        /// <summary> Итоговая детализация отклонения изготовления. См. <see cref="EffectiveSetupDetail"/>. </summary>
        public string EffectiveMachiningDetail =>
            HasMachiningReasonOverride ? MachiningReasonOverrideComment : MasterMachiningDetail;

        /// <summary> null, когда переопределения нет — WPF тогда просто не показывает тултип. </summary>
        public string? SetupOverrideTooltip => BuildOverrideTooltip(
            MasterSetupComment, SetupReasonOverride, SetupReasonOverrideComment,
            SetupReasonOverrideIsMasterFault, SetupReasonOverrideMasterFaultComment);

        /// <summary> null, когда переопределения нет. См. <see cref="SetupOverrideTooltip"/>. </summary>
        public string? MachiningOverrideTooltip => BuildOverrideTooltip(
            MasterMachiningComment, MachiningReasonOverride, MachiningReasonOverrideComment,
            MachiningReasonOverrideIsMasterFault, MachiningReasonOverrideMasterFaultComment);

        private string? BuildOverrideTooltip(string masterReason, string overrideReason, string overrideComment,
            bool isMasterFault, string masterFaultComment)
        {
            if (string.IsNullOrWhiteSpace(overrideReason)) return null;

            var master = string.IsNullOrWhiteSpace(masterReason) ? "не указана" : $"«{masterReason}»";
            var who = string.IsNullOrWhiteSpace(ReasonOverrideBy) ? "" : $" ({ReasonOverrideBy}";
            if (who.Length > 0)
                who += ReasonOverrideAt.HasValue ? $", {ReasonOverrideAt.Value:dd.MM.yy})" : ")";

            var sb = new StringBuilder($"Мастер: {master} → СГТ: «{overrideReason}»{who}");
            if (!string.IsNullOrWhiteSpace(overrideComment))
                sb.Append($"\n\n{overrideComment}");
            if (isMasterFault)
            {
                sb.Append("\n\nОшибка мастера.");
                if (!string.IsNullOrWhiteSpace(masterFaultComment))
                    sb.Append($" {masterFaultComment}");
            }
            return sb.ToString();
        }
        #endregion


        private double _ExcludedOperationsTime;
        /// <summary> Суммарное время исключённых операций при изменении техпроцесса </summary>
        public double ExcludedOperationsTime
        {
            get => _ExcludedOperationsTime;
            set
            {
                if (Set(ref _ExcludedOperationsTime, value))
                {
                    NeedUpdate = true;
                    OnPropertyChanged(nameof(NeedUpdate));
                }
            }
        }

        private string _IncreaseReason;
        /// <summary> Причина увеличения норматива </summary>
        public string IncreaseReason
        {
            get => _IncreaseReason;
            set
            {
                if (Set(ref _IncreaseReason, value))
                {
                    NeedUpdate = true;
                    OnPropertyChanged(nameof(NeedUpdate));
                }
            }
        }


        private bool _ExcludeFromReports;
        /// <summary> Исключать ли деталь из расчетов в отчетах </summary>
        public bool ExcludeFromReports
        {
            get => _ExcludeFromReports;
            set
            {
                if (Set(ref _ExcludeFromReports, value))
                {
                    NeedUpdate = true;
                    OnPropertyChanged(nameof(NeedUpdate));
                }
            }
        }

        private bool _NeedUpdate;
        /// <summary> Описание </summary>
        public bool NeedUpdate
        {
            get => _NeedUpdate;
            set { 
                if (Set(ref _NeedUpdate, value))
                {
                    OnPropertyChanged(nameof(SetupTimeFact));
                    OnPropertyChanged(nameof(ProductionTimeFact));
                    OnPropertyChanged(nameof(SetupRatio));
                    OnPropertyChanged(nameof(SetupRatioTitle));
                    OnPropertyChanged(nameof(ProductionRatio));
                    OnPropertyChanged(nameof(ProductionRatioTitle));
                    OnPropertyChanged(nameof(SingleProductionTime));
                    OnPropertyChanged(nameof(SpecifiedDowntimesRatio));
                    OnPropertyChanged(nameof(MasterSetupComment));
                    OnPropertyChanged(nameof(MasterMachiningComment));
                    OnPropertyChanged(nameof(MasterComment));
                    OnPropertyChanged(nameof(MasterSetupDetail));
                    OnPropertyChanged(nameof(MasterMachiningDetail));
                    OnPropertyChanged(nameof(EffectiveSetupDetail));
                    OnPropertyChanged(nameof(EffectiveMachiningDetail));
                    OnPropertyChanged(nameof(Error));
                    OnPropertyChanged(nameof(NeedUpdate));
                }
            }
        }

        private string _LongSetupReasonComment;
        public string LongSetupReasonComment
        {
            get => _LongSetupReasonComment;
            set
            {
                if (Set(ref _LongSetupReasonComment, value))
                {
                    NeedUpdate = true;
                    OnPropertyChanged(nameof(NeedUpdate));
                }
            }
        }

        private string _LongSetupFixComment;
        public string LongSetupFixComment
        {
            get => _LongSetupFixComment;
            set
            {
                if (Set(ref _LongSetupFixComment, value))
                {
                    NeedUpdate = true;
                    OnPropertyChanged(nameof(NeedUpdate));
                }
            }
        }

        private string _LongSetupEngeneerComment;
        public string LongSetupEngeneerComment
        {
            get => _LongSetupEngeneerComment;
            set
            {
                if (Set(ref _LongSetupEngeneerComment, value))
                {
                    NeedUpdate = true;
                    OnPropertyChanged(nameof(NeedUpdate));
                }
            }
        }

        private bool _IsFlagged;
        /// <summary>
        /// Помечена ли строка аналитиком как проблемная (только в памяти, не сохраняется).
        /// Используется для визуальной маркировки при первичной проверке.
        /// </summary>
        public bool IsFlagged
        {
            get => _IsFlagged;
            set => Set(ref _IsFlagged, value);
        }

        private AiCheckStatus _AiCheckStatus;
        /// <summary>
        /// Статус фоновой ИИ-проверки комментариев мастера (фича AiMasterCheck).
        /// Только в памяти, не сохраняется и НЕ трогает NeedUpdate — вердикт
        /// совещательный и эфемерный, строка от него не становится «изменённой».
        /// </summary>
        public AiCheckStatus AiCheckStatus
        {
            get => _AiCheckStatus;
            set => Set(ref _AiCheckStatus, value);
        }

        private string _AiCheckRemark = string.Empty;
        /// <summary>
        /// Текст замечания ИИ-проверки (или подсказка о недоступности проверки).
        /// Только в памяти, не сохраняется, NeedUpdate не трогает.
        /// </summary>
        public string AiCheckRemark
        {
            get => _AiCheckRemark;
            set => Set(ref _AiCheckRemark, value);
        }


        public double SingleProductionTime => FinishedCountFact > 0 ? ProductionTimeFact / FinishedCountFact : 0;

        //public double SetupRatio => PartialSetupTime == 0 
        //    ? SetupTimePlanForCalc / SetupTimeFact 
        //    : SetupTimePlanForCalc * DomainSettings.MaxSetupLimits.GetValueOrDefault(Machine, DomainSettings.FallbackMaxSetupLimitValue) < PartialSetupTime
        //        ? SetupTimePlanForCalc / PartialSetupTime 
        //        : 0;

        public double SetupRatio => SetupTimePlanForCalc / SetupTimeFact;

        public double SetupRatioIncludeDowntimes => SetupTimeFact > 0 ? SetupTimePlanForCalc / (SetupTimeFact + SetupDowntimes) : 0;
        public string SetupRatioTitle => SetupRatio is double.NaN or double.PositiveInfinity 
            ? "б/н" 
            : SetupRatio > DomainSettings.MaxSetupLimits.GetValueOrDefault(Machine, DomainSettings.FallbackMaxSetupLimitValue) 
                ? $"{SetupRatio:0%}\n({DomainSettings.MaxSetupLimits.GetValueOrDefault(Machine, DomainSettings.FallbackMaxSetupLimitValue):0%})" 
                : $"{SetupRatio:0%}";
        // Единственная деталь партии при наличии наладки (полноценной или частичной
        // завершающей) считается выполненной в наладке: изготовления не было → б/и,
        // а не КПД 0%.
        public double ProductionRatio => FinishedCount > 0 && FinishedCountFact == 0
            ? double.NaN
            : FinishedCountFact * ProductionTimePlanForCalc / ProductionTimeFact;
        public string ProductionRatioTitle => ProductionRatio is double.NaN or double.PositiveInfinity or double.NegativeInfinity ? "б/и" : $"{ProductionRatio:0%}";
        public double SpecifiedDowntimesRatio => (SetupDowntimes + MachiningDowntimes) / (EndMachiningTime - StartSetupTime).TotalMinutes;
        public double PartReplacementTime => SingleProductionTime == 0 ? 0 : SingleProductionTime - MachiningTime.TotalMinutes;
        public double PlanForBatch => FinishedCountFact * ProductionTimePlanForCalc;
        private DateTime FixedDate(DateTime dateTime)
        {
            var year = ShiftDate.Year;
            var month = ShiftDate.Month;
            var day = ShiftDate.Day;
            var hour = dateTime.Hour;
            var minute = dateTime.Minute;
            var fixedDateTime = new DateTime(year, month, day, hour, minute, 0);
            var diff = (fixedDateTime - ShiftDate.AddHours(8)).TotalMinutes;
            if (diff <= 0 && Shift == Shifts.Night) fixedDateTime = fixedDateTime.AddDays(1);
            return fixedDateTime;
        }

        

        public Dictionary<string, bool> SetupReasonsRequireComment =>
            DomainSettings.SetupReasons.ToDictionary(x => x.Reason, x => x.RequireComment);
        public Dictionary<string, bool> MachiningReasonsRequireComment =>
            DomainSettings.MachiningReasons.ToDictionary(x => x.Reason, x => x.RequireComment);

        private static bool RequiresComment(string? comment, Dictionary<string, bool> reasonsDict)
        {
            return !string.IsNullOrWhiteSpace(comment) &&
                   reasonsDict.TryGetValue(comment, out bool requiresComment) &&
                   requiresComment;
        }

        /// <summary>
        /// Штучная партия по регламенту «Требования к заполнению и контролю»:
        /// м/в &lt; 3 мин и изготовлено ≤ 10 деталей, либо м/в ≥ 3 мин и ≤ 5 деталей.
        /// Считается по FinishedCountFact (изготовлено с учётом наладки) — тому же
        /// значению, что идёт в КПД и отчёты. Штучные партии не участвуют в отчётах
        /// по изготовлению.
        /// </summary>
        public bool IsSmallBatch =>
            (MachiningTime.TotalMinutes < 3 && FinishedCountFact <= 10)
            || (MachiningTime.TotalMinutes >= 3 && FinishedCountFact <= 5);

        /// <summary>
        /// Есть реальный заказ (не «Без М/Л» и не пустой). Норматив привязан к
        /// заказу/техпроцессу, а не к тому, была ли выполнена работа в эту смену —
        /// используется, чтобы требовать объяснение отсутствующего норматива
        /// независимо от б/н/б/и/частичной наладки (2026-07-21).
        /// </summary>
        private bool HasOrder => !string.IsNullOrWhiteSpace(Order) && !Order.EqualsOrdinalIgnoreCase("Без М/Л");

        public string Error
        {
            get
            {
                if (string.IsNullOrWhiteSpace(this[nameof(SpecifiedDowntimesComment)]) &&
                    string.IsNullOrWhiteSpace(this[nameof(MasterSetupComment)]) &&
                    string.IsNullOrWhiteSpace(this[nameof(MasterMachiningComment)]) &&
                    string.IsNullOrWhiteSpace(this[nameof(MasterSetupDetail)]) &&
                    string.IsNullOrWhiteSpace(this[nameof(MasterMachiningDetail)]))
                {
                    return null!;
                }
                else
                {
                    return "Ошибка валидации";
                }
            }
        }

        // Строки из справочника cnc_deviation_reasons — используются валидацией причин.
        // При переименовании причины в справочнике их нужно поправить здесь же.
        private const string NoNormativesReason = "Отсутствие нормативов";
        private const string WrongNormativesReason = "Некорректные нормативы";
        private const string NotByProcessReason = "Изготовление не по техпроцессу";

        /// <summary>
        /// Причины из общей части справочника (Type=None, доступны в ОБЕИХ категориях). Ниже
        /// подмешиваются и в наладку, и в изготовление: одна и та же причина обязана
        /// валидироваться одинаково независимо от того, в каком комбобоксе выбрана. Добавление
        /// сюда автоматически распространяется на обе категории — руками дублировать не нужно.
        /// </summary>
        private static readonly string[] CommonReasonsRequiringNormative =
        {
            "Неопытный оператор",
            "Работа ученика",
            "Небрежное отношение к работе",
            "Особенности изготовления",
        };

        /// <summary>
        /// Причины наладки, которые объясняют ОТКЛОНЕНИЕ от норматива, а значит требуют, чтобы
        /// норматив существовал. При его отсутствии объяснять нечего — там своя причина.
        /// </summary>
        private static readonly string[] SetupReasonsRequiringNormative =
            new[]
            {
                "Освоение",
                "Изготовление типовой детали",
            }
            .Concat(CommonReasonsRequiringNormative).ToArray();

        /// <summary> То же для изготовления. См. <see cref="SetupReasonsRequiringNormative"/>. </summary>
        private static readonly string[] MachiningReasonsRequiringNormative =
            new[]
            {
                "Штучная/длительная работа",
                "Разовое изменение времени из-за проблем с инструментом/оборудованием",
                "Несоответствующие заготовки",
            }
            .Concat(CommonReasonsRequiringNormative).ToArray();

        // Причины, при которых модель обязана свериться с историей детали
        // (зеркало HistorySensitiveReasons в AiService PromptBuilder).
        private static readonly string[] AiHistorySensitiveReasons =
        {
            "Освоение",
            "Некорректные нормативы",
            "Отсутствие нормативов",
        };

        /// <summary>
        /// Причина из справочника комбобокса самодостаточна по регламенту — модели
        /// проверять нечего: конкретика, если нужна (RequireComment), проверяется
        /// отдельной аномалией по MasterComment, а некорректные комбинации
        /// («Изготовление типовой детали» при низком КПД и т.п.) режет валидация.
        /// Исключение — history-sensitive причины: их модель сверяет с историей.
        /// </summary>
        private static bool IsSelfSufficientReason(string? comment, Dictionary<string, bool> reasonsDict) =>
            !string.IsNullOrWhiteSpace(comment)
            && reasonsDict.ContainsKey(comment)
            && !AiHistorySensitiveReasons.Contains(comment.Trim(), StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Аномалии строки, требующие комментария мастера — для фоновой ИИ-проверки
        /// релевантности (фича AiMasterCheck). Условия зеркалят this[columnName],
        /// но БЕЗ клаузы «комментарий пуст»: пустые ловит валидация, ИИ проверяет
        /// смысл уже заполненного. Field — имя поля комментария, Description —
        /// текст аномалии с числами (уходит в промпт).
        /// </summary>
        public List<(string Field, string Description)> GetAiCheckAnomalies()
        {
            var result = new List<(string, string)>();

            // Аномалии ниже привязаны к *Detail* (не к голому значению комбобокса) — иначе
            // AiMasterCheck просит модель проверить, объясняет ли САМА СТРОКА «Освоение»
            // числовую аномалию, что бессмысленно и путает модель (найдено на проде: деталь
            // с MasterSetupComment=«Освоение», MasterSetupDetail=«деталь с квазера» — модель
            // проверяла «Освоение» как «комментарий», деталь-детализацию не видела как ответ).
            // Причину категории переопределил аналитик: заполнение мастера заменено решением СГТ,
            // просить у мастера пояснений по этой категории не нужно.
            if (!HasSetupReasonOverride && !IsSelfSufficientReason(MasterSetupComment, SetupReasonsRequireComment))
            {
                if (SetupTimePlanForCalc <= 0 && HasOrder)
                    result.Add((nameof(MasterSetupDetail), "Отсутствует норматив наладки при реальном заказе"));
                if ((SetupRatio < 0.695 || SetupRatio > DomainSettings.MaxSetupLimit) && SetupTimeFact > 0 && (SetupTimePlanForCalc > 0 || HasOrder))
                    result.Add((nameof(MasterSetupDetail), $"КПД наладки {SetupRatio:0%} вне нормы (70–{DomainSettings.MaxSetupLimit:0%})"));
                if (PartialSetupTime > 0 && SetupTimePlanForCalc > 0 && PartialSetupTime > SetupTimePlanForCalc / 0.695)
                    result.Add((nameof(MasterSetupDetail), $"Частичная наладка {PartialSetupTime:0} мин превышает норматив {SetupTimePlanForCalc:0} мин"));
            }

            if (!HasMachiningReasonOverride && !IsSelfSufficientReason(MasterMachiningComment, MachiningReasonsRequireComment))
            {
                if (ProductionTimePlanForCalc <= 0 && HasOrder)
                    result.Add((nameof(MasterMachiningDetail), "Отсутствует норматив изготовления при реальном заказе"));
                if (ProductionRatio is < 0.695 or > 1.2 && (ProductionTimePlanForCalc > 0 || HasOrder))
                    result.Add((nameof(MasterMachiningDetail), $"КПД изготовления {ProductionRatio:0%} вне нормы (70–120%)"));
            }

            if (!HasSetupReasonOverride && RequiresComment(MasterSetupComment, SetupReasonsRequireComment))
                result.Add((nameof(MasterSetupDetail), $"Причина отклонения в наладке «{MasterSetupComment}» требует уточнения в комментарии"));
            if (!HasMachiningReasonOverride && RequiresComment(MasterMachiningComment, MachiningReasonsRequireComment))
                result.Add((nameof(MasterMachiningDetail), $"Причина отклонения в изготовлении «{MasterMachiningComment}» требует уточнения в комментарии"));

            if (SpecifiedDowntimesRatio > 0.5)
                result.Add((nameof(SpecifiedDowntimesComment), $"Простои {SpecifiedDowntimesRatio:0%} — более 50% времени работы"));

            return result;
        }

        /// <summary>
        /// Строка готова к ИИ-проверке: есть аномалии и все требуемые комментарии
        /// заполнены (Error непуст ровно тогда, когда какой-то из них пуст или
        /// причина выбрана некорректно — такие случаи закрывает валидация).
        /// </summary>
        public bool IsReadyForAiCheck => string.IsNullOrWhiteSpace(Error) && GetAiCheckAnomalies().Count > 0;

        public string this[string columnName]
        {
            get
            {
                // Ячейки причин в гриде привязаны к Effective*Reason (показывают переопределение
                // СГТ, если оно есть) — валидируется ИТОГОВАЯ причина: решение СГТ при наличии
                // переопределения, иначе отметка мастера. Ошибка мастера, уже исправленная
                // переопределением СГТ, на строке больше не висит.
                columnName = columnName switch
                {
                    nameof(EffectiveSetupReason) => nameof(MasterSetupComment),
                    nameof(EffectiveMachiningReason) => nameof(MasterMachiningComment),
                    nameof(EffectiveSetupDetail) => nameof(MasterSetupDetail),
                    nameof(EffectiveMachiningDetail) => nameof(MasterMachiningDetail),
                    _ => columnName,
                };

                // Итоговые значения с учётом переопределения СГТ — именно их проверяет
                // дневной ИИ-анализ (финальная причина), и именно они валидируются здесь.
                // Построчная ИИ-проверка мастера (GetAiCheckAnomalies) по-прежнему работает
                // только по отметкам мастера и не затрагивается.
                var setupReason = HasSetupReasonOverride ? SetupReasonOverride : MasterSetupComment;
                var machiningReason = HasMachiningReasonOverride ? MachiningReasonOverride : MasterMachiningComment;
                var setupDetail = HasSetupReasonOverride ? SetupReasonOverrideComment : MasterSetupDetail;
                var machiningDetail = HasMachiningReasonOverride ? MachiningReasonOverrideComment : MasterMachiningDetail;

                return columnName switch
                {
                    // Норматив=0 при реальном заказе требует объяснения ВСЕГДА, независимо от того,
                    // была ли выполнена работа в эту смену (б/н/б/и/частичная наладка) — норматив
                    // привязан к заказу/техпроцессу, а не к факту работы (см. HasOrder).
                    nameof(MasterSetupComment) when string.IsNullOrWhiteSpace(setupReason) && SetupTimePlanForCalc <= 0 && HasOrder => "Необходимо указать причину отсутствия норматива наладки.",
                    nameof(MasterSetupComment) when string.IsNullOrWhiteSpace(setupReason) && (SetupRatio < 0.695 || SetupRatio > DomainSettings.MaxSetupLimit) && SetupTimeFact > 0 && (SetupTimePlanForCalc > 0 || HasOrder) => "Необходимо указать причину отклонения от норматива наладки.",
                    nameof(MasterSetupComment) when string.IsNullOrWhiteSpace(setupReason) && PartialSetupTime > 0 && SetupTimePlanForCalc > 0 && PartialSetupTime > SetupTimePlanForCalc / 0.695 => "Необходимо указать причину превышения частичной наладки.",
                    nameof(MasterMachiningComment) when string.IsNullOrWhiteSpace(machiningReason) && ProductionTimePlanForCalc <= 0 && HasOrder => "Необходимо указать причину отсутствия норматива изготовления.",
                    nameof(MasterMachiningComment) when string.IsNullOrWhiteSpace(machiningReason) && ProductionRatio is < 0.695 or > 1.2 && (ProductionTimePlanForCalc > 0 || HasOrder) => "Необходимо указать причину отклонения от норматива изготовления.",
                    // Проверки «причина против отсутствия норматива» идут ПЕРЕД частными правилами
                    // ниже: при нулевом нормативе КПД вырождается в 0, и правило про «Изготовление
                    // типовой детали» сработало бы первым, сообщив про низкий показатель вместо
                    // настоящей проблемы — норматива нет вовсе.
                    //
                    // Норматива нет — объяснять нечего: отклонения от него не существует, пока
                    // самого норматива нет. Единственные корректные причины в этом случае —
                    // «Отсутствие нормативов» (норматив не установлен) либо «Изготовление не по
                    // техпроцессу» (норматив есть, но от другой операции/рабочего центра).
                    nameof(MasterSetupComment) when
                        !string.IsNullOrWhiteSpace(setupReason)
                        && SetupReasonsRequiringNormative.Contains(setupReason)
                        && SetupTimePlanForCalc <= 0
                        => $"Причина «{setupReason}» не объясняет отсутствие норматива наладки — укажите «{NoNormativesReason}» или «{NotByProcessReason}».",
                    nameof(MasterMachiningComment) when
                        !string.IsNullOrWhiteSpace(machiningReason)
                        && MachiningReasonsRequiringNormative.Contains(machiningReason)
                        && ProductionTimePlanForCalc <= 0
                        => $"Причина «{machiningReason}» не объясняет отсутствие норматива изготовления — укажите «{NoNormativesReason}» или «{NotByProcessReason}».",

                    // Зеркальные проверки: «Отсутствие нормативов» при заданном нормативе — прямое
                    // противоречие факту, а «Некорректные нормативы» при отсутствующем: нельзя
                    // назвать некорректным то, чего нет, это «Отсутствие нормативов».
                    nameof(MasterSetupComment) when
                        setupReason == NoNormativesReason && SetupTimePlanForCalc > 0
                        => $"«{NoNormativesReason}» неприменимо: норматив наладки задан.",
                    nameof(MasterMachiningComment) when
                        machiningReason == NoNormativesReason && ProductionTimePlanForCalc > 0
                        => $"«{NoNormativesReason}» неприменимо: норматив изготовления задан.",
                    nameof(MasterSetupComment) when
                        setupReason == WrongNormativesReason && SetupTimePlanForCalc <= 0
                        => $"«{WrongNormativesReason}» неприменимо: норматива наладки нет — это «{NoNormativesReason}».",
                    nameof(MasterMachiningComment) when
                        machiningReason == WrongNormativesReason && ProductionTimePlanForCalc <= 0
                        => $"«{WrongNormativesReason}» неприменимо: норматива изготовления нет — это «{NoNormativesReason}».",

                    nameof(MasterSetupComment) when
                        !string.IsNullOrWhiteSpace(setupReason)
                        && setupReason == "Изготовление типовой детали"
                        && ((SetupTimeFact > 0 && SetupRatio < 0.695)
                            || (PartialSetupTime > 0 && SetupTimePlanForCalc > 0 && PartialSetupTime > SetupTimePlanForCalc / 0.695))
                        => "«Изготовление типовой детали» объясняет только превышение норматива наладки (>200%) — не низкий показатель и не превышение частичной наладки",
                    nameof(MasterMachiningComment) when
                        !string.IsNullOrWhiteSpace(machiningReason)
                        && machiningReason == "Штучная/длительная работа"
                        && FinishedCount > 0
                        && !IsSmallBatch
                        => "Причина «Штучная/длительная работа» применима только при малой партии: м/в < 3 мин и изготовлено ≤ 10 деталей или м/в ≥ 3 мин и изготовлено ≤ 5 деталей",

                    nameof(MasterSetupDetail) when string.IsNullOrWhiteSpace(setupDetail) &&
                                        RequiresComment(setupReason, SetupReasonsRequireComment) => "Требуется указать дополнительный комментарий для выбранной причины наладки.",
                    nameof(MasterMachiningDetail) when string.IsNullOrWhiteSpace(machiningDetail) &&
                                        RequiresComment(machiningReason, MachiningReasonsRequireComment) => "Требуется указать дополнительный комментарий для выбранной причины изготовления.",
                    nameof(SpecifiedDowntimesComment) when string.IsNullOrWhiteSpace(SpecifiedDowntimesComment) && SpecifiedDowntimesRatio > 0.5 => "Необходимо дать комментарий т.к. простой более 50%.",
                    _ => null!,
                };
            }
        }
    }
}
