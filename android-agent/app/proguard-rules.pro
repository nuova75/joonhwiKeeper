# kotlinx.serialization
-keepattributes *Annotation*, InnerClasses
-keepclassmembers class com.xkeep.agent.** {
    *** Companion;
}
-keepclasseswithmembers class com.xkeep.agent.** {
    kotlinx.serialization.KSerializer serializer(...);
}
# DeviceAdminReceiver 는 매니페스트에서 참조되므로 이름 유지
-keep class com.xkeep.agent.admin.AgentAdminReceiver
